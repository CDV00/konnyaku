using Konnyaku.Application.Persistence;
using Konnyaku.Application.Tenancy;
using Konnyaku.Crm.Contracts;
using Konnyaku.Crm.Domain;
using Konnyaku.Identity.Contracts;
using Konnyaku.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Konnyaku.Crm.Application;

public interface ICrmDbContext
{
    DbSet<Lead> Leads { get; }
    DbSet<StudentProfile> Students { get; }

    DbSet<CaptchaRedemption> CaptchaRedemptions { get; }

    void Publish(IIntegrationEvent integrationEvent, string? correlationId = null);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Public registration form. <see cref="Website"/> is a honeypot: humans never fill it. <see cref="Captcha"/> is the
/// solved proof-of-work challenge, required while <see cref="CaptchaOptions.Enabled"/>.
/// </summary>
public sealed record SubmitLeadCommand(
    string StudentName, int? BirthYear, string? GuardianName, string? Phone, string? Email, string LanguageCode,
    string? Goal, Guid? PreferredBranchId, string? PreferredSchedule, string? ConsentVersion, bool ConsentGiven,
    IReadOnlyDictionary<string, string>? Utm, string? Locale, string? Website, CaptchaSolution? Captcha = null);

public sealed record LeadAccepted(Guid? LeadId);

public sealed class SubmitLeadHandler(
    ICrmDbContext db, ITenantContext tenant, IIdentityApi identity, CaptchaService captcha, IDbErrorClassifier errors, TimeProvider clock)
{
    public const string ConsultantRole = "Consultant";

    public async Task<Result<LeadAccepted>> HandleAsync(SubmitLeadCommand command, CancellationToken cancellationToken)
    {
        if (tenant.TenantId is null)
        {
            return Error.Validation("crm.tenant_unknown");
        }

        if (!string.IsNullOrEmpty(command.Website))
        {
            // Bot: answer like a success so it cannot learn the rule, store nothing.
            return new LeadAccepted(null);
        }

        CaptchaTicket? ticket = null;
        if (captcha.Enabled)
        {
            var verified = captcha.Verify(command.Captcha);
            if (!verified.IsSuccess)
            {
                return verified.Error!;
            }

            ticket = verified.Value;
        }

        if (!command.ConsentGiven)
        {
            return Error.Validation("crm.consent_required");
        }

        var now = clock.GetUtcNow();
        var details = new LeadDetails(command.StudentName ?? string.Empty, command.BirthYear, command.GuardianName, command.Phone,
            command.Email, command.LanguageCode ?? string.Empty, command.Goal, command.PreferredBranchId, command.PreferredSchedule,
            command.Utm, command.Locale);

        var phone = Lead.NormalizePhone(command.Phone);
        var email = command.Email?.Trim().ToLowerInvariant();
        var existing = await db.Leads.FirstOrDefaultAsync(l =>
            (l.Status == LeadStatus.New || l.Status == LeadStatus.Contacted) &&
            ((phone != null && l.Phone == phone) || (email != null && l.Email == email)), cancellationToken);
        if (existing is not null)
        {
            var updated = existing.Resubmit(details, now);
            if (!updated.IsSuccess)
            {
                return updated.Error!;
            }

            return await SaveAsync(ticket, now, cancellationToken) is { } resubmitError ? resubmitError : new LeadAccepted(existing.Id);
        }

        var created = Lead.Create(details, "web", command.ConsentVersion ?? string.Empty, now);
        if (!created.IsSuccess)
        {
            return created.Error!;
        }

        var lead = created.Value;
        lead.AssignTo(await PickConsultantAsync(cancellationToken), now);
        db.Leads.Add(lead);
        db.Publish(new LeadCreatedV1(lead.Id, lead.LanguageCode, lead.AssignedConsultantId, lead.FollowUpDueAt, lead.Locale));
        return await SaveAsync(ticket, now, cancellationToken) is { } error ? error : new LeadAccepted(lead.Id);
    }

    /// <summary>
    /// Saves the form together with the captcha redemption, so a challenge is spent only when a form is accepted
    /// (validation errors keep it usable) and two submissions with the same solution cannot both pass.
    /// </summary>
    private async Task<Error?> SaveAsync(CaptchaTicket? ticket, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (ticket is null)
        {
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }

        await db.CaptchaRedemptions.Where(r => r.ExpiresAt < now).ExecuteDeleteAsync(cancellationToken);
        db.CaptchaRedemptions.Add(new CaptchaRedemption(ticket.Challenge, ticket.ExpiresAt));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DbUpdateException exception) when (errors.IsUniqueViolation(exception))
        {
            return Error.Conflict("crm.captcha_used");
        }
    }

    /// <summary>The consultant with the fewest open leads (round-robin by load).</summary>
    private async Task<Guid?> PickConsultantAsync(CancellationToken cancellationToken)
    {
        var consultants = await identity.ListUsersWithRoleAsync(ConsultantRole, cancellationToken);
        if (consultants.Count == 0)
        {
            return null;
        }

        var load = await db.Leads
            .Where(l => l.AssignedConsultantId != null && (l.Status == LeadStatus.New || l.Status == LeadStatus.Contacted))
            .GroupBy(l => l.AssignedConsultantId!.Value)
            .Select(g => new { ConsultantId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ConsultantId, x => x.Count, cancellationToken);
        return consultants.OrderBy(c => load.GetValueOrDefault(c)).First();
    }
}

public sealed record LeadView(
    Guid Id, string StudentName, int? BirthYear, string? GuardianName, string? Phone, string? Email, string LanguageCode,
    string? Goal, string Status, Guid? AssignedConsultantId, DateTimeOffset? FollowUpDueAt, DateTimeOffset CreatedAt);

public sealed class LeadQueries(ICrmDbContext db)
{
    public async Task<IReadOnlyList<LeadView>> ListAsync(bool openOnly, CancellationToken cancellationToken)
    {
        var query = db.Leads.AsNoTracking();
        if (openOnly)
        {
            query = query.Where(l => l.Status == LeadStatus.New || l.Status == LeadStatus.Contacted);
        }

        return await query.OrderBy(l => l.FollowUpDueAt ?? l.CreatedAt)
            .Select(l => new LeadView(l.Id, l.StudentName, l.BirthYear, l.GuardianName, l.Phone, l.Email, l.LanguageCode,
                l.Goal, l.Status.ToString(), l.AssignedConsultantId, l.FollowUpDueAt, l.CreatedAt))
            .Take(500)
            .ToListAsync(cancellationToken);
    }
}

public sealed record ChangeLeadStatusCommand(LeadStatus Status);

public sealed class ChangeLeadStatusHandler(ICrmDbContext db, TimeProvider clock)
{
    public async Task<Result<bool>> HandleAsync(Guid leadId, ChangeLeadStatusCommand command, CancellationToken cancellationToken)
    {
        var lead = await db.Leads.FirstOrDefaultAsync(l => l.Id == leadId, cancellationToken);
        if (lead is null)
        {
            return Error.NotFound("crm.lead_not_found");
        }

        var result = lead.ChangeStatus(command.Status, clock.GetUtcNow());
        if (!result.IsSuccess)
        {
            return result.Error!;
        }

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}

public sealed record RegisterStudentCommand(
    string FullName, int? BirthYear, string? GuardianName, string? Phone, string? Email, Guid? HomeBranchId,
    string? CommunicationLocale, Guid? LeadId, Guid? UserId);

public sealed class RegisterStudentHandler(ICrmDbContext db, TimeProvider clock)
{
    public async Task<Result<StudentDto>> HandleAsync(RegisterStudentCommand command, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var created = StudentProfile.Create(command.FullName, command.BirthYear, command.GuardianName, command.Phone,
            command.Email, command.HomeBranchId, command.CommunicationLocale ?? "vi", command.LeadId, now);
        if (!created.IsSuccess)
        {
            return created.Error!;
        }

        var student = created.Value;
        if (command.UserId is { } userId)
        {
            student.LinkAccount(userId);
        }

        if (command.LeadId is { } leadId && await db.Leads.FirstOrDefaultAsync(l => l.Id == leadId, cancellationToken) is { IsOpen: true } lead)
        {
            lead.ChangeStatus(LeadStatus.Converted, now);
        }

        db.Students.Add(student);
        db.Publish(new StudentRegisteredV1(student.Id, command.LeadId, command.HomeBranchId));
        await db.SaveChangesAsync(cancellationToken);
        return student.ToDto();
    }
}

public sealed class CrmQueries(ICrmDbContext db) : ICrmApi
{
    public async Task<StudentDto?> GetStudentAsync(Guid studentId, CancellationToken cancellationToken)
    {
        var student = await db.Students.AsNoTracking().FirstOrDefaultAsync(s => s.Id == studentId, cancellationToken);
        return student?.ToDto();
    }

    public async Task<IReadOnlyList<StudentDto>> ListStudentsOfUserAsync(Guid userId, CancellationToken cancellationToken) =>
        (await db.Students.AsNoTracking().Where(s => s.UserId == userId).ToListAsync(cancellationToken))
        .Select(s => s.ToDto()).ToList();

    public async Task<IReadOnlyList<StudentDto>> ListStudentsAsync(CancellationToken cancellationToken) =>
        (await db.Students.AsNoTracking().OrderBy(s => s.FullName).Take(500).ToListAsync(cancellationToken))
        .Select(s => s.ToDto()).ToList();
}

public static class StudentMapping
{
    public static StudentDto ToDto(this StudentProfile s) =>
        new(s.Id, s.FullName, s.BirthYear, s.HomeBranchId, s.UserId, s.CommunicationLocale);
}
