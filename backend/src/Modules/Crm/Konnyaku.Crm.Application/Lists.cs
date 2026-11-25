using Konnyaku.Application.Authorization;
using Konnyaku.Application.Persistence;
using Konnyaku.Crm.Domain;
using Konnyaku.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Konnyaku.Crm.Application;

/// <summary>The staff member behind a request, as the access log records them (from the token, never from the client).</summary>
public sealed record RequestActor(Guid? UserId, string? DisplayName, string? IpAddress)
{
    public static readonly RequestActor System = new(null, null, null);
}

/// <summary>
/// Filters of the student list (<c>GET /api/crm/students</c>). <see cref="Q"/> searches name, phone, email and guardian
/// without accents; <see cref="Status"/> is <c>active</c>, <c>inactive</c> or empty (all); <see cref="Branch"/> is a
/// branch id or <c>none</c>; <see cref="Ids"/> looks up given students (comma-separated, for name lookups of other screens).
/// </summary>
public sealed record StudentFilter(
    string? Q = null, string? Status = null, string? Branch = null, bool? HasAccount = null, DateOnly? CreatedFrom = null,
    DateOnly? CreatedTo = null, string? Ids = null)
{
    public IReadOnlyDictionary<string, string> ForLog() => LogFilter.Of(
        ("q", PersonalDataMask.Term(Q)), ("status", Status), ("branch", Branch), ("hasAccount", HasAccount?.ToString()),
        ("createdFrom", CreatedFrom?.ToString("O")), ("createdTo", CreatedTo?.ToString("O")));
}

/// <summary>
/// Filters of the lead list (<c>GET /api/crm/leads</c>). <see cref="Status"/>: <c>open</c> (New + Contacted, default),
/// a status name, or <c>all</c>.
/// </summary>
public sealed record LeadFilter(
    string? Q = null, string? Status = null, string? Language = null, string? Branch = null, Guid? Consultant = null,
    DateOnly? CreatedFrom = null, DateOnly? CreatedTo = null)
{
    public IReadOnlyDictionary<string, string> ForLog() => LogFilter.Of(
        ("q", PersonalDataMask.Term(Q)), ("status", Status), ("language", Language), ("branch", Branch),
        ("consultant", Consultant?.ToString()), ("createdFrom", CreatedFrom?.ToString("O")), ("createdTo", CreatedTo?.ToString("O")));
}

internal static class LogFilter
{
    public static IReadOnlyDictionary<string, string> Of(params (string Key, string? Value)[] values) =>
        values.Where(v => !string.IsNullOrWhiteSpace(v.Value)).ToDictionary(v => v.Key, v => v.Value!);
}

/// <summary>A row of the student list. Birth year, guardian, phone and email are always masked here.</summary>
public sealed record StudentListItem(
    Guid Id, string FullName, string? BirthYear, string? GuardianName, string? Phone, string? Email, Guid? HomeBranchId, Guid? UserId,
    string CommunicationLocale, bool IsActive, bool Anonymized, DateTimeOffset CreatedAt);

/// <summary>A row of the lead list. Birth year, guardian, phone and email are always masked here.</summary>
public sealed record LeadListItem(
    Guid Id, string StudentName, string? BirthYear, string? GuardianName, string? Phone, string? Email, string LanguageCode,
    string? Goal, string Status, Guid? AssignedConsultantId, DateTimeOffset? FollowUpDueAt, DateTimeOffset CreatedAt,
    Guid? PreferredBranchId, string? PreferredSchedule, bool Anonymized);

/// <summary>Full contact details of one record, returned by the logged "show" action.</summary>
public sealed record ContactView(Guid Id, int? BirthYear, string? GuardianName, string? Phone, string? Email);

public sealed record RevealCommand(string? Purpose);

/// <summary>
/// The student and lead lists: filtered, sorted and paged in the database (trigram index on the folded search column),
/// contact details masked. The access rules are the endpoint's; the lead list is narrowed to the member's branches.
/// </summary>
public sealed class CrmListQueries(ICrmDbContext db, IAccessControl access)
{
    public static readonly IReadOnlySet<string> StudentSorts = new HashSet<string> { "name", "createdAt", "birthYear" };
    public static readonly IReadOnlySet<string> LeadSorts = new HashSet<string> { "due", "createdAt", "name" };

    public async Task<Page<StudentListItem>> ListStudentsAsync(StudentFilter filter, PageRequest page, CancellationToken ct)
    {
        var query = StudentQuery(filter);
        var ordered = (page.Sort, page.Descending) switch
        {
            ("createdAt", false) => query.OrderBy(s => s.CreatedAt).ThenBy(s => s.Id),
            ("createdAt", true) => query.OrderByDescending(s => s.CreatedAt).ThenByDescending(s => s.Id),
            ("birthYear", false) => query.OrderBy(s => s.BirthYear).ThenBy(s => s.FullName).ThenBy(s => s.Id),
            ("birthYear", true) => query.OrderByDescending(s => s.BirthYear).ThenBy(s => s.FullName).ThenBy(s => s.Id),
            (_, true) => query.OrderByDescending(s => s.FullName).ThenByDescending(s => s.Id),
            _ => query.OrderBy(s => s.FullName).ThenBy(s => s.Id),
        };
        return (await ordered.ToPageAsync(page, ct)).Map(ToListItem);
    }

    /// <summary>Students matching the filter, for an export (no paging, capped by the caller).</summary>
    internal IQueryable<StudentProfile> StudentQuery(StudentFilter filter, IReadOnlyList<Guid>? branchScope = null)
    {
        var query = db.Students.AsNoTracking();
        if (TextSearch.Term(filter.Q) is { } term)
        {
            var pattern = TextSearch.Contains(term);
            query = query.Where(s => EF.Functions.Like(s.SearchText, pattern));
        }

        query = filter.Status switch
        {
            "active" => query.Where(s => s.IsActive),
            "inactive" => query.Where(s => !s.IsActive),
            _ => query,
        };
        if (filter.Branch == "none")
        {
            query = query.Where(s => s.HomeBranchId == null);
        }
        else if (Guid.TryParse(filter.Branch, out var branchId))
        {
            query = query.Where(s => s.HomeBranchId == branchId);
        }

        if (filter.HasAccount is { } hasAccount)
        {
            query = hasAccount ? query.Where(s => s.UserId != null) : query.Where(s => s.UserId == null);
        }

        if (filter.CreatedFrom is { } from)
        {
            var start = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            query = query.Where(s => s.CreatedAt >= start);
        }

        if (filter.CreatedTo is { } to)
        {
            var end = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            query = query.Where(s => s.CreatedAt < end);
        }

        if (ParseIds(filter.Ids) is { } ids)
        {
            query = query.Where(s => ids.Contains(s.Id));
        }

        if (branchScope is not null)
        {
            var branches = branchScope.ToArray();
            query = query.Where(s => s.HomeBranchId != null && branches.Contains(s.HomeBranchId.Value));
        }

        return query;
    }

    public async Task<Page<LeadListItem>> ListLeadsAsync(LeadFilter filter, PageRequest page, CancellationToken ct)
    {
        if (await LeadScopeAsync(Permissions.Leads, ct) is not { } scope)
        {
            return Page<LeadListItem>.Empty(page);
        }

        var query = LeadQuery(filter, scope);
        var ordered = (page.Sort, page.Descending) switch
        {
            ("createdAt", false) => query.OrderBy(l => l.CreatedAt).ThenBy(l => l.Id),
            ("createdAt", true) => query.OrderByDescending(l => l.CreatedAt).ThenByDescending(l => l.Id),
            ("name", false) => query.OrderBy(l => l.StudentName).ThenBy(l => l.Id),
            ("name", true) => query.OrderByDescending(l => l.StudentName).ThenByDescending(l => l.Id),
            (_, true) => query.OrderByDescending(l => l.FollowUpDueAt ?? l.CreatedAt).ThenByDescending(l => l.Id),
            _ => query.OrderBy(l => l.FollowUpDueAt ?? l.CreatedAt).ThenBy(l => l.Id),
        };
        return (await ordered.ToPageAsync(page, ct)).Map(ToListItem);
    }

    /// <summary>Null when the member holds no grant of <paramref name="permission"/>; an empty list means center-wide.</summary>
    internal async Task<IReadOnlyList<Guid>?> LeadScopeAsync(string permission, CancellationToken ct)
    {
        var scope = (await access.ListAsync(ct)).FirstOrDefault(s => s.Permission == permission);
        return scope is null ? null : scope.TenantWide ? [] : scope.BranchIds;
    }

    /// <summary>Leads matching the filter within <paramref name="branchScope"/> (empty: every branch).</summary>
    internal IQueryable<Lead> LeadQuery(LeadFilter filter, IReadOnlyList<Guid> branchScope)
    {
        var query = db.Leads.AsNoTracking();
        if (branchScope.Count > 0)
        {
            var branches = branchScope.ToArray();
            query = query.Where(l => l.PreferredBranchId != null && branches.Contains(l.PreferredBranchId.Value));
        }

        if (TextSearch.Term(filter.Q) is { } term)
        {
            var pattern = TextSearch.Contains(term);
            query = query.Where(l => EF.Functions.Like(l.SearchText, pattern));
        }

        query = filter.Status switch
        {
            "all" => query,
            null or "" or "open" => query.Where(l => l.Status == LeadStatus.New || l.Status == LeadStatus.Contacted),
            var s when Enum.TryParse<LeadStatus>(s, ignoreCase: true, out var status) => query.Where(l => l.Status == status),
            _ => query.Where(_ => false),
        };
        if (!string.IsNullOrWhiteSpace(filter.Language))
        {
            query = query.Where(l => l.LanguageCode == filter.Language);
        }

        if (filter.Branch == "none")
        {
            query = query.Where(l => l.PreferredBranchId == null);
        }
        else if (Guid.TryParse(filter.Branch, out var branchId))
        {
            query = query.Where(l => l.PreferredBranchId == branchId);
        }

        if (filter.Consultant is { } consultant)
        {
            query = query.Where(l => l.AssignedConsultantId == consultant);
        }

        if (filter.CreatedFrom is { } from)
        {
            var start = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            query = query.Where(l => l.CreatedAt >= start);
        }

        if (filter.CreatedTo is { } to)
        {
            var end = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            query = query.Where(l => l.CreatedAt < end);
        }

        return query;
    }

    private static Guid[]? ParseIds(string? ids)
    {
        if (string.IsNullOrWhiteSpace(ids))
        {
            return null;
        }

        return ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(id => Guid.TryParse(id, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .Distinct()
            .Take(PageRequest.MaxPageSize)
            .ToArray();
    }

    public static StudentListItem ToListItem(StudentProfile s) => new(
        s.Id, s.FullName, PersonalDataMask.BirthYear(s.BirthYear), PersonalDataMask.Name(s.GuardianName), PersonalDataMask.Phone(s.Phone),
        PersonalDataMask.Email(s.Email), s.HomeBranchId, s.UserId, s.CommunicationLocale, s.IsActive, s.AnonymizedAt is not null, s.CreatedAt);

    public static LeadListItem ToListItem(Lead l) => new(
        l.Id, l.StudentName, PersonalDataMask.BirthYear(l.BirthYear), PersonalDataMask.Name(l.GuardianName), PersonalDataMask.Phone(l.Phone),
        PersonalDataMask.Email(l.Email), l.LanguageCode, l.Goal, l.Status.ToString(), l.AssignedConsultantId, l.FollowUpDueAt, l.CreatedAt,
        l.PreferredBranchId, l.PreferredSchedule, l.AnonymizedAt is not null);
}

/// <summary>Writes the personal data access log (its own save: a reveal is logged even if nothing else is saved).</summary>
public sealed class PersonalDataLog(ICrmDbContext db, TimeProvider clock)
{
    public void Add(PersonalDataAction action, string subjectType, Guid? subjectId, RequestActor actor, string? purpose = null,
        IReadOnlyDictionary<string, string>? filter = null, int? rowCount = null) =>
        db.PersonalDataAccesses.Add(new PersonalDataAccess(action, subjectType, subjectId, purpose, filter, rowCount, actor.UserId,
            actor.DisplayName, actor.IpAddress, clock.GetUtcNow()));

    public async Task WriteAsync(PersonalDataAction action, string subjectType, Guid? subjectId, RequestActor actor, CancellationToken ct,
        string? purpose = null, IReadOnlyDictionary<string, string>? filter = null, int? rowCount = null)
    {
        Add(action, subjectType, subjectId, actor, purpose, filter, rowCount);
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>
/// The "show" button: full contact details of one student or lead, for members who keep those records, one record at a
/// time, every time logged (who, which record, why). Lists never carry them.
/// </summary>
public sealed class RevealHandlers(ICrmDbContext db, IAccessControl access, PersonalDataLog log)
{
    public static readonly IReadOnlySet<string> Purposes = new HashSet<string> { "view", "edit", "contact" };

    public async Task<Result<ContactView>> RevealStudentAsync(Guid id, RevealCommand command, RequestActor actor, CancellationToken ct)
    {
        var student = await db.Students.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (student is null)
        {
            return Error.NotFound("crm.student_not_found");
        }

        await log.WriteAsync(PersonalDataAction.Reveal, "student", id, actor, ct, Purpose(command));
        return new ContactView(student.Id, student.BirthYear, student.GuardianName, student.Phone, student.Email);
    }

    public async Task<Result<ContactView>> RevealLeadAsync(Guid id, RevealCommand command, RequestActor actor, CancellationToken ct)
    {
        var lead = await db.Leads.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id, ct);
        if (lead is null)
        {
            return Error.NotFound("crm.lead_not_found");
        }

        if (await access.EnsureAsync(Permissions.Leads, ct, lead.PreferredBranchId) is { } forbidden)
        {
            return forbidden;
        }

        await log.WriteAsync(PersonalDataAction.Reveal, "lead", id, actor, ct, Purpose(command));
        return new ContactView(lead.Id, lead.BirthYear, lead.GuardianName, lead.Phone, lead.Email);
    }

    private static string Purpose(RevealCommand command) =>
        command.Purpose is { } p && Purposes.Contains(p) ? p : "view";
}
