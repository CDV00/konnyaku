using System.Text.Json;
using System.Text.Json.Serialization;
using Konnyaku.Application.Persistence;
using Konnyaku.Application.Privacy;
using Konnyaku.Crm.Contracts;
using Konnyaku.Crm.Domain;
using Konnyaku.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Konnyaku.Crm.Application;

public sealed record CreateDataSubjectRequestCommand(
    string SubjectType, Guid SubjectId, DataSubjectRequestType Type, string? Note, DateTimeOffset? ReceivedAt);

public sealed record ResolveDataSubjectRequestCommand(DataSubjectRequestStatus Status, string? Resolution);

public sealed record AnonymizeCommand(Guid? RequestId);

public sealed record DataSubjectRequestView(
    Guid Id, string SubjectType, Guid SubjectId, string? SubjectName, string Type, string Status, string? Note, DateTimeOffset ReceivedAt,
    DateTimeOffset DueAt, bool Overdue, DateTimeOffset? ResolvedAt, string? ResolvedByName, string? Resolution);

public sealed record PersonalDataAccessView(
    Guid Id, string Action, string SubjectType, Guid? SubjectId, string? SubjectName, string? Purpose,
    IReadOnlyDictionary<string, string> Filter, int? RowCount, Guid? ActorId, string? ActorName, string? IpAddress, DateTimeOffset OccurredAt);

public sealed record AccessLogFilter(string? Action = null, Guid? Actor = null, Guid? Subject = null, DateOnly? From = null, DateOnly? To = null);

public sealed record RequestFilter(string? Status = null, string? Type = null);

/// <summary>
/// Data subject requests (Nghị định 13/2023, Luật Bảo vệ dữ liệu cá nhân): the tenant admin records the request with its
/// deadline, answers an access request with the export file, an erasure request by anonymizing, and closes the request.
/// </summary>
public sealed class PrivacyHandlers(
    ICrmDbContext db, PersonalDataLog log, PrivacySettings settings, TimeProvider clock, IEnumerable<IPersonalDataSource> sources)
{
    public static readonly JsonSerializerOptions ExportJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<Page<DataSubjectRequestView>> ListRequestsAsync(RequestFilter filter, PageRequest page, CancellationToken ct)
    {
        var query = db.DataSubjectRequests.AsNoTracking();
        if (Enum.TryParse<DataSubjectRequestStatus>(filter.Status, true, out var status))
        {
            query = query.Where(r => r.Status == status);
        }

        if (Enum.TryParse<DataSubjectRequestType>(filter.Type, true, out var type))
        {
            query = query.Where(r => r.Type == type);
        }

        // Open requests first, the most urgent on top; closed ones newest first.
        var result = await query
            .OrderBy(r => r.Status != DataSubjectRequestStatus.Open)
            .ThenBy(r => r.Status == DataSubjectRequestStatus.Open ? r.DueAt : DateTimeOffset.MaxValue)
            .ThenByDescending(r => r.ReceivedAt).ThenBy(r => r.Id)
            .ToPageAsync(page, ct);
        var names = await NamesAsync(result.Items.Select(r => (r.SubjectType, (Guid?)r.SubjectId)), ct);
        var now = clock.GetUtcNow();
        return result.Map(r => new DataSubjectRequestView(r.Id, r.SubjectType, r.SubjectId, names.GetValueOrDefault(r.SubjectId),
            r.Type.ToString(), r.Status.ToString(), r.Note, r.ReceivedAt, r.DueAt, r.Status == DataSubjectRequestStatus.Open && r.DueAt < now,
            r.ResolvedAt, r.ResolvedByName, r.Resolution));
    }

    public async Task<Result<Guid>> CreateRequestAsync(CreateDataSubjectRequestCommand command, CancellationToken ct)
    {
        if (!await SubjectExistsAsync(command.SubjectType, command.SubjectId, ct))
        {
            return Error.NotFound("crm.subject_not_found");
        }

        var now = clock.GetUtcNow();
        var created = DataSubjectRequest.Create(command.SubjectType, command.SubjectId, command.Type, command.Note, command.ReceivedAt ?? now,
            TimeSpan.FromHours(settings.RequestDueHours), now);
        if (!created.IsSuccess)
        {
            return created.Error!;
        }

        db.DataSubjectRequests.Add(created.Value);
        await db.SaveChangesAsync(ct);
        return created.Value.Id;
    }

    public async Task<Result<bool>> ResolveRequestAsync(Guid id, ResolveDataSubjectRequestCommand command, RequestActor actor, CancellationToken ct)
    {
        var request = await db.DataSubjectRequests.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (request is null)
        {
            return Error.NotFound("crm.request_not_found");
        }

        var resolved = request.Resolve(command.Status, command.Resolution, actor.UserId, actor.DisplayName, clock.GetUtcNow());
        if (!resolved.IsSuccess)
        {
            return resolved.Error!;
        }

        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Everything the center holds about one student, module by module (JSON): profile, the lead they came from,
    /// account, enrollments, attendance, orders, payments and lesson ledger. Logged.
    /// </summary>
    public async Task<Result<ExportFile>> ExportStudentAsync(Guid studentId, RequestActor actor, CancellationToken ct)
    {
        var student = await db.Students.AsNoTracking().FirstOrDefaultAsync(s => s.Id == studentId, ct);
        if (student is null)
        {
            return Error.NotFound("crm.student_not_found");
        }

        var subject = new PersonalDataSubject(student.Id, student.UserId);
        var sections = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            sections[source.Section] = await source.ExportAsync(subject, ct);
        }

        return await ExportFileAsync("student", studentId, sections, actor, ct);
    }

    public async Task<Result<ExportFile>> ExportLeadAsync(Guid leadId, RequestActor actor, CancellationToken ct)
    {
        var lead = await db.Leads.AsNoTracking().FirstOrDefaultAsync(l => l.Id == leadId, ct);
        if (lead is null)
        {
            return Error.NotFound("crm.lead_not_found");
        }

        var requests = await RequestsAboutAsync(leadId, ct);
        return await ExportFileAsync("lead", leadId, new SortedDictionary<string, object?> { ["crm"] = new { lead = CrmPersonalData.LeadData(lead), requests } },
            actor, ct);
    }

    /// <summary>
    /// Erasure: overwrites the student's personal data and that of the lead they came from, blanks the values in their
    /// change history, and tells the other modules (learner account, meeting names). Lesson ledger, orders, payments and
    /// attendance keep their rows: they carry the student id only and accounting law requires them.
    /// </summary>
    public async Task<Result<bool>> AnonymizeStudentAsync(Guid studentId, AnonymizeCommand command, RequestActor actor, CancellationToken ct)
    {
        var student = await db.Students.FirstOrDefaultAsync(s => s.Id == studentId, ct);
        if (student is null)
        {
            return Error.NotFound("crm.student_not_found");
        }

        var request = await RequestToCloseAsync(command.RequestId, "student", studentId, ct);
        if (!request.IsSuccess)
        {
            return request.Error!;
        }

        var now = clock.GetUtcNow();
        var anonymized = student.Anonymize(now);
        if (!anonymized.IsSuccess)
        {
            return anonymized.Error!;
        }

        var lead = student.LeadId is { } leadId ? await db.Leads.FirstOrDefaultAsync(l => l.Id == leadId, ct) : null;
        lead?.Anonymize(now);
        request.Value?.Resolve(DataSubjectRequestStatus.Completed, null, actor.UserId, actor.DisplayName, now);
        db.Publish(new StudentAnonymizedV1(student.Id, student.UserId));
        log.Add(PersonalDataAction.Anonymize, "student", student.Id, actor);

        await using var transaction = await db.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        await db.RedactChangeLogAsync(student.Id, ct);
        if (lead is not null)
        {
            await db.RedactChangeLogAsync(lead.Id, ct);
        }

        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task<Result<bool>> AnonymizeLeadAsync(Guid leadId, AnonymizeCommand command, RequestActor actor, CancellationToken ct)
    {
        var lead = await db.Leads.FirstOrDefaultAsync(l => l.Id == leadId, ct);
        if (lead is null)
        {
            return Error.NotFound("crm.lead_not_found");
        }

        var request = await RequestToCloseAsync(command.RequestId, "lead", leadId, ct);
        if (!request.IsSuccess)
        {
            return request.Error!;
        }

        var now = clock.GetUtcNow();
        lead.Anonymize(now);
        request.Value?.Resolve(DataSubjectRequestStatus.Completed, null, actor.UserId, actor.DisplayName, now);
        log.Add(PersonalDataAction.Anonymize, "lead", lead.Id, actor);

        await using var transaction = await db.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        await db.RedactChangeLogAsync(lead.Id, ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task<Page<PersonalDataAccessView>> ListAccessAsync(AccessLogFilter filter, PageRequest page, CancellationToken ct)
    {
        var query = db.PersonalDataAccesses.AsNoTracking();
        if (Enum.TryParse<PersonalDataAction>(filter.Action, true, out var action))
        {
            query = query.Where(a => a.Action == action);
        }

        if (filter.Actor is { } actor)
        {
            query = query.Where(a => a.ActorId == actor);
        }

        if (filter.Subject is { } subject)
        {
            query = query.Where(a => a.SubjectId == subject);
        }

        if (filter.From is { } from)
        {
            var start = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            query = query.Where(a => a.OccurredAt >= start);
        }

        if (filter.To is { } to)
        {
            var end = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            query = query.Where(a => a.OccurredAt < end);
        }

        var result = await query.OrderByDescending(a => a.OccurredAt).ThenByDescending(a => a.Id).ToPageAsync(page, ct);
        var names = await NamesAsync(result.Items.Select(a => (a.SubjectType, a.SubjectId)), ct);
        return result.Map(a => new PersonalDataAccessView(a.Id, a.Action.ToString(), a.SubjectType, a.SubjectId,
            a.SubjectId is { } id ? names.GetValueOrDefault(id) : null, a.Purpose, a.Filter, a.RowCount, a.ActorId, a.ActorName, a.IpAddress,
            a.OccurredAt));
    }

    private async Task<Result<ExportFile>> ExportFileAsync(
        string subjectType, Guid subjectId, IReadOnlyDictionary<string, object?> sections, RequestActor actor, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var document = new
        {
            subjectType,
            subjectId,
            exportedAt = now,
            exportedBy = PersonalDataMask.Name(actor.DisplayName),
            data = sections,
        };
        await log.WriteAsync(PersonalDataAction.SubjectExport, subjectType, subjectId, actor, ct);
        return new ExportFile($"{subjectType}-{subjectId.ToString("N")[..8]}-{now:yyyyMMdd}.json", "application/json; charset=utf-8",
            JsonSerializer.SerializeToUtf8Bytes(document, ExportJson));
    }

    private async Task<Result<DataSubjectRequest?>> RequestToCloseAsync(Guid? requestId, string subjectType, Guid subjectId, CancellationToken ct)
    {
        if (requestId is not { } id)
        {
            return (DataSubjectRequest?)null;
        }

        var request = await db.DataSubjectRequests.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (request is null || request.SubjectType != subjectType || request.SubjectId != subjectId)
        {
            return Error.NotFound("crm.request_not_found");
        }

        return request.Status == DataSubjectRequestStatus.Open ? request : Error.Conflict("crm.request_closed");
    }

    internal async Task<IReadOnlyList<object>> RequestsAboutAsync(Guid subjectId, CancellationToken ct) =>
        await db.DataSubjectRequests.AsNoTracking().Where(r => r.SubjectId == subjectId).OrderBy(r => r.ReceivedAt)
            .Select(r => (object)new { type = r.Type.ToString(), status = r.Status.ToString(), r.ReceivedAt, r.DueAt, r.ResolvedAt })
            .ToListAsync(ct);

    private async Task<bool> SubjectExistsAsync(string subjectType, Guid id, CancellationToken ct) => subjectType switch
    {
        "student" => await db.Students.AnyAsync(s => s.Id == id, ct),
        "lead" => await db.Leads.AnyAsync(l => l.Id == id, ct),
        _ => false,
    };

    private async Task<IReadOnlyDictionary<Guid, string>> NamesAsync(IEnumerable<(string Type, Guid? Id)> subjects, CancellationToken ct)
    {
        var list = subjects.Where(s => s.Id is not null).ToList();
        var studentIds = list.Where(s => s.Type == "student").Select(s => s.Id!.Value).Distinct().ToArray();
        var leadIds = list.Where(s => s.Type == "lead").Select(s => s.Id!.Value).Distinct().ToArray();
        var names = new Dictionary<Guid, string>();
        if (studentIds.Length > 0)
        {
            foreach (var s in await db.Students.AsNoTracking().Where(s => studentIds.Contains(s.Id)).Select(s => new { s.Id, s.FullName }).ToListAsync(ct))
            {
                names[s.Id] = s.FullName;
            }
        }

        if (leadIds.Length > 0)
        {
            foreach (var l in await db.Leads.AsNoTracking().Where(l => leadIds.Contains(l.Id)).Select(l => new { l.Id, l.StudentName }).ToListAsync(ct))
            {
                names[l.Id] = l.StudentName;
            }
        }

        return names;
    }
}

/// <summary>What Crm holds about a student: the profile, the lead they came from, requests they made.</summary>
public sealed class CrmPersonalData(ICrmDbContext db) : IPersonalDataSource
{
    public string Section => "crm";

    public async Task<object?> ExportAsync(PersonalDataSubject subject, CancellationToken cancellationToken)
    {
        var student = await db.Students.AsNoTracking().FirstOrDefaultAsync(s => s.Id == subject.StudentId, cancellationToken);
        if (student is null)
        {
            return null;
        }

        var lead = student.LeadId is { } leadId ? await db.Leads.AsNoTracking().FirstOrDefaultAsync(l => l.Id == leadId, cancellationToken) : null;
        var requests = await db.DataSubjectRequests.AsNoTracking().Where(r => r.SubjectId == student.Id).OrderBy(r => r.ReceivedAt)
            .Select(r => new { type = r.Type.ToString(), status = r.Status.ToString(), r.ReceivedAt, r.DueAt, r.ResolvedAt })
            .ToListAsync(cancellationToken);
        return new
        {
            profile = new
            {
                student.Id, student.FullName, student.BirthYear, student.GuardianName, student.Phone, student.Email, student.HomeBranchId,
                student.CommunicationLocale, student.IsActive, student.CreatedAt, student.AnonymizedAt,
            },
            lead = lead is null ? null : LeadData(lead),
            requests,
        };
    }

    internal static object LeadData(Lead lead) => new
    {
        lead.Id, lead.StudentName, lead.BirthYear, lead.GuardianName, lead.Phone, lead.Email, lead.LanguageCode, lead.Goal,
        lead.PreferredBranchId, lead.PreferredSchedule, lead.Source, lead.Utm, lead.ConsentVersion, lead.ConsentAt,
        status = lead.Status.ToString(), lead.CreatedAt, lead.UpdatedAt, lead.AnonymizedAt,
    };
}

/// <summary>
/// Retention: a lead that never became a student is kept for <see cref="PrivacySettings.LeadRetentionMonths"/> after its
/// last change, then anonymized (the row stays for statistics). Run per tenant by the Worker; every lead is logged.
/// </summary>
public sealed class LeadRetention(ICrmDbContext db, PersonalDataLog log, PrivacySettings settings, TimeProvider clock)
{
    private const int BatchSize = 100;

    public async Task<int> RunAsync(CancellationToken ct)
    {
        if (settings.LeadRetentionMonths <= 0)
        {
            return 0;
        }

        var total = 0;
        while (true)
        {
            var now = clock.GetUtcNow();
            var cutoff = now.AddMonths(-settings.LeadRetentionMonths);
            var batch = await db.Leads
                .Where(l => l.AnonymizedAt == null && l.Status != LeadStatus.Converted && l.UpdatedAt < cutoff)
                .OrderBy(l => l.UpdatedAt).ThenBy(l => l.Id)
                .Take(BatchSize)
                .ToListAsync(ct);
            if (batch.Count == 0)
            {
                return total;
            }

            foreach (var lead in batch)
            {
                lead.Anonymize(now);
                log.Add(PersonalDataAction.RetentionAnonymize, "lead", lead.Id, RequestActor.System, $"{settings.LeadRetentionMonths}m");
            }

            await using var transaction = await db.BeginTransactionAsync(ct);
            await db.SaveChangesAsync(ct);
            foreach (var lead in batch)
            {
                await db.RedactChangeLogAsync(lead.Id, ct);
            }

            await transaction.CommitAsync(ct);
            total += batch.Count;
        }
    }
}
