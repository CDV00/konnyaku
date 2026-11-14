using Konnyaku.SharedKernel;

namespace Konnyaku.Crm.Domain;

/// <summary>A prospective student who filled the public registration form (or was entered by staff).</summary>
public sealed class Lead : Entity, ITenantOwned
{
    public static readonly TimeSpan CallBackWithin = TimeSpan.FromMinutes(15);

    private Lead()
    {
    }

    public Guid TenantId { get; private set; }
    public string StudentName { get; private set; } = string.Empty;
    public int? BirthYear { get; private set; }
    public string? GuardianName { get; private set; }
    public string? Phone { get; private set; }
    public string? Email { get; private set; }
    public string LanguageCode { get; private set; } = string.Empty;
    public string? Goal { get; private set; }
    public Guid? PreferredBranchId { get; private set; }
    public string? PreferredSchedule { get; private set; }
    public string Source { get; private set; } = "web";
    public Dictionary<string, string> Utm { get; private set; } = [];
    public string ConsentVersion { get; private set; } = string.Empty;
    public DateTimeOffset ConsentAt { get; private set; }
    public string Locale { get; private set; } = "vi";
    public LeadStatus Status { get; private set; }
    public Guid? AssignedConsultantId { get; private set; }
    public DateTimeOffset? FollowUpDueAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public bool IsOpen => Status is LeadStatus.New or LeadStatus.Contacted;

    public static Result<Lead> Create(LeadDetails details, string source, string consentVersion, DateTimeOffset now)
    {
        var validation = Validate(details, now);
        if (validation is not null)
        {
            return validation;
        }

        if (string.IsNullOrWhiteSpace(consentVersion))
        {
            return Error.Validation("crm.consent_required");
        }

        var lead = new Lead
        {
            Source = source,
            ConsentVersion = consentVersion,
            ConsentAt = now,
            Status = LeadStatus.New,
            CreatedAt = now,
        };
        lead.Apply(details, now);
        return lead;
    }

    /// <summary>The same person submitting again refreshes the open lead instead of creating a duplicate.</summary>
    public Result<Lead> Resubmit(LeadDetails details, DateTimeOffset now)
    {
        var validation = Validate(details, now);
        if (validation is not null)
        {
            return validation;
        }

        Apply(details, now);
        return this;
    }

    public void AssignTo(Guid? consultantId, DateTimeOffset now)
    {
        AssignedConsultantId = consultantId;
        FollowUpDueAt = now.Add(CallBackWithin);
    }

    public Result<Lead> ChangeStatus(LeadStatus status, DateTimeOffset now)
    {
        if (!IsOpen)
        {
            return Error.Conflict("crm.lead_closed");
        }

        Status = status;
        UpdatedAt = now;
        if (!IsOpen)
        {
            FollowUpDueAt = null;
        }

        return this;
    }

    private void Apply(LeadDetails details, DateTimeOffset now)
    {
        StudentName = details.StudentName.Trim();
        BirthYear = details.BirthYear;
        GuardianName = details.GuardianName?.Trim();
        Phone = NormalizePhone(details.Phone);
        Email = details.Email?.Trim().ToLowerInvariant();
        LanguageCode = details.LanguageCode.Trim();
        Goal = details.Goal?.Trim();
        PreferredBranchId = details.PreferredBranchId;
        PreferredSchedule = details.PreferredSchedule?.Trim();
        Utm = details.Utm?.ToDictionary() ?? [];
        Locale = details.Locale ?? "vi";
        UpdatedAt = now;
    }

    private static Error? Validate(LeadDetails d, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(d.StudentName))
        {
            return Error.Validation("crm.student_name_required");
        }

        if (string.IsNullOrWhiteSpace(d.Phone) && string.IsNullOrWhiteSpace(d.Email))
        {
            return Error.Validation("crm.contact_required");
        }

        if (string.IsNullOrWhiteSpace(d.LanguageCode))
        {
            return Error.Validation("crm.language_required");
        }

        var age = d.BirthYear is { } year ? now.Year - year : (int?)null;
        if (age is < 0 or > 120)
        {
            return Error.Validation("crm.birth_year_invalid");
        }

        // Minors need a guardian (contact and contract); see docs 7.9.4.
        if (age < 18 && string.IsNullOrWhiteSpace(d.GuardianName))
        {
            return Error.Validation("crm.guardian_required");
        }

        return null;
    }

    public static string? NormalizePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
        {
            return null;
        }

        var digits = new string(phone.Where(c => char.IsDigit(c) || c == '+').ToArray());
        return digits.StartsWith("+84", StringComparison.Ordinal) ? "0" + digits[3..] : digits;
    }
}

public sealed record LeadDetails(
    string StudentName, int? BirthYear, string? GuardianName, string? Phone, string? Email, string LanguageCode,
    string? Goal, Guid? PreferredBranchId, string? PreferredSchedule, IReadOnlyDictionary<string, string>? Utm, string? Locale);

public enum LeadStatus
{
    New,
    Contacted,
    Converted,
    Lost,
}

/// <summary>Student record of a tenant. Linked to a sign-in account (Identity user) when one is issued.</summary>
public sealed class StudentProfile : Entity, ITenantOwned
{
    private StudentProfile()
    {
    }

    public Guid TenantId { get; private set; }
    public string FullName { get; private set; } = string.Empty;
    public int? BirthYear { get; private set; }
    public string? GuardianName { get; private set; }
    public string? Phone { get; private set; }
    public string? Email { get; private set; }
    public Guid? HomeBranchId { get; private set; }
    public Guid? UserId { get; private set; }
    public string CommunicationLocale { get; private set; } = "vi";
    public Guid? LeadId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static Result<StudentProfile> Create(
        string fullName, int? birthYear, string? guardianName, string? phone, string? email, Guid? homeBranchId,
        string communicationLocale, Guid? leadId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(fullName))
        {
            return Error.Validation("crm.student_name_required");
        }

        return new StudentProfile
        {
            FullName = fullName.Trim(),
            BirthYear = birthYear,
            GuardianName = guardianName?.Trim(),
            Phone = Lead.NormalizePhone(phone),
            Email = email?.Trim().ToLowerInvariant(),
            HomeBranchId = homeBranchId,
            CommunicationLocale = communicationLocale,
            LeadId = leadId,
            CreatedAt = now,
        };
    }

    public void LinkAccount(Guid userId) => UserId = userId;
}
