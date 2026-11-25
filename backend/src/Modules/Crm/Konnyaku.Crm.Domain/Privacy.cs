using Konnyaku.SharedKernel;

namespace Konnyaku.Crm.Domain;

/// <summary>What was done with personal data (Nghị định 13/2023: the controller keeps a record of processing).</summary>
public enum PersonalDataAction
{
    /// <summary>A member showed the full contact details of one record (the "show" button, or opening the edit form).</summary>
    Reveal,

    /// <summary>A list left the system as a CSV file.</summary>
    Export,

    /// <summary>A list was opened for printing.</summary>
    Print,

    /// <summary>All data held about a student was exported for a data subject request.</summary>
    SubjectExport,

    /// <summary>The personal data of a record was erased on request.</summary>
    Anonymize,

    /// <summary>The retention job erased a lead that was never converted.</summary>
    RetentionAnonymize,
}

/// <summary>
/// One access to personal data. Append-only (the module role cannot update or delete rows) and free of personal data
/// itself: who (the staff member), what, which record or which filter, how many rows, from where — never the values.
/// </summary>
public sealed class PersonalDataAccess : ITenantOwned
{
    private PersonalDataAccess()
    {
    }

    public PersonalDataAccess(
        PersonalDataAction action, string subjectType, Guid? subjectId, string? purpose, IReadOnlyDictionary<string, string>? filter,
        int? rowCount, Guid? actorId, string? actorName, string? ipAddress, DateTimeOffset at)
    {
        Id = Guid.CreateVersion7();
        Action = action;
        SubjectType = subjectType;
        SubjectId = subjectId;
        Purpose = purpose is { Length: > 40 } ? purpose[..40] : purpose;
        Filter = filter?.ToDictionary() ?? [];
        RowCount = rowCount;
        ActorId = actorId;
        ActorName = actorName;
        IpAddress = ipAddress;
        OccurredAt = at;
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public PersonalDataAction Action { get; private set; }

    /// <summary><c>student</c>, <c>lead</c> (one record) or <c>students</c>, <c>leads</c> (a list).</summary>
    public string SubjectType { get; private set; } = string.Empty;

    public Guid? SubjectId { get; private set; }
    public string? Purpose { get; private set; }

    /// <summary>Filters of an exported or printed list; a search term is stored masked.</summary>
    public Dictionary<string, string> Filter { get; private set; } = [];

    public int? RowCount { get; private set; }

    /// <summary>Null for the system (retention job).</summary>
    public Guid? ActorId { get; private set; }

    public string? ActorName { get; private set; }
    public string? IpAddress { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
}

public enum DataSubjectRequestType
{
    /// <summary>See the data held (answered with the export file).</summary>
    Access,

    /// <summary>Correct the data (done by editing the record).</summary>
    Rectification,

    /// <summary>Delete the data (done by anonymizing: ledger and financial records are kept by law).</summary>
    Erasure,

    /// <summary>Withdraw consent to processing (a lead is anonymized; a student's contract data stays for the contract).</summary>
    ConsentWithdrawal,
}

public enum DataSubjectRequestStatus
{
    Open,
    Completed,
    Rejected,
}

/// <summary>
/// A request from a student, parent or lead about their personal data, recorded by the tenant admin with a deadline
/// (72 hours by default, Nghị định 13/2023 Art. 14–16) and closed with what was done.
/// </summary>
public sealed class DataSubjectRequest : Entity, ITenantOwned, IAuditable
{
    private DataSubjectRequest()
    {
    }

    public Guid TenantId { get; private set; }

    /// <summary><c>student</c> or <c>lead</c>.</summary>
    public string SubjectType { get; private set; } = string.Empty;

    public Guid SubjectId { get; private set; }
    public DataSubjectRequestType Type { get; private set; }
    public DataSubjectRequestStatus Status { get; private set; }

    /// <summary>How the request arrived and what exactly is asked (staff note: no contact details needed here).</summary>
    public string? Note { get; private set; }

    public DateTimeOffset ReceivedAt { get; private set; }
    public DateTimeOffset DueAt { get; private set; }
    public DateTimeOffset? ResolvedAt { get; private set; }
    public Guid? ResolvedById { get; private set; }
    public string? ResolvedByName { get; private set; }
    public string? Resolution { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    Guid IAuditable.AuditSubjectId => Id;

    public static readonly IReadOnlySet<string> SubjectTypes = new HashSet<string> { "student", "lead" };

    public static Result<DataSubjectRequest> Create(
        string subjectType, Guid subjectId, DataSubjectRequestType type, string? note, DateTimeOffset receivedAt, TimeSpan dueWithin,
        DateTimeOffset now)
    {
        if (!SubjectTypes.Contains(subjectType))
        {
            return Error.Validation("crm.subject_type_invalid");
        }

        if (receivedAt > now.AddMinutes(5))
        {
            return Error.Validation("crm.received_in_future");
        }

        return new DataSubjectRequest
        {
            SubjectType = subjectType,
            SubjectId = subjectId,
            Type = type,
            Status = DataSubjectRequestStatus.Open,
            Note = Trim(note),
            ReceivedAt = receivedAt,
            DueAt = receivedAt.Add(dueWithin),
            CreatedAt = now,
        };
    }

    public Result<DataSubjectRequest> Resolve(DataSubjectRequestStatus status, string? resolution, Guid? actorId, string? actorName, DateTimeOffset now)
    {
        if (Status != DataSubjectRequestStatus.Open)
        {
            return Error.Conflict("crm.request_closed");
        }

        if (status == DataSubjectRequestStatus.Open)
        {
            return Error.Validation("crm.request_status_invalid");
        }

        if (status == DataSubjectRequestStatus.Rejected && string.IsNullOrWhiteSpace(resolution))
        {
            return Error.Validation("crm.request_reason_required");
        }

        Status = status;
        Resolution = Trim(resolution);
        ResolvedAt = now;
        ResolvedById = actorId;
        ResolvedByName = actorName;
        return this;
    }

    private static string? Trim(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim() is { Length: > 1000 } t ? t[..1000] : text.Trim();
}
