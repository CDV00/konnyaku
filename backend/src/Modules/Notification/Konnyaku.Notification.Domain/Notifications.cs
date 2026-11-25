using Konnyaku.SharedKernel;

namespace Konnyaku.Notification.Domain;

public enum NotificationStatus
{
    /// <summary>Waiting for (another) delivery attempt.</summary>
    Pending,
    Sent,

    /// <summary>Gave up: permanent error from the provider, or too many transient ones. Can be resent by an admin.</summary>
    Failed,
}

/// <summary>
/// One message to one recipient on one channel. Rendered at send time from its template, in its locale, with its data.
/// <see cref="DeduplicationKey"/> is unique per tenant: an event delivered twice queues the message once.
/// </summary>
public sealed class NotificationMessage : Entity, ITenantOwned
{
    public const int MaxErrorLength = 1000;

    private NotificationMessage()
    {
    }

    public Guid TenantId { get; private set; }
    public string Channel { get; private set; } = string.Empty;
    public string TemplateKey { get; private set; } = string.Empty;
    public Guid? RecipientUserId { get; private set; }
    public string RecipientAddress { get; private set; } = string.Empty;
    public string? RecipientName { get; private set; }
    public string Locale { get; private set; } = "vi";
    public Dictionary<string, string> Data { get; private set; } = [];
    public bool Sensitive { get; private set; }
    public string DeduplicationKey { get; private set; } = string.Empty;
    public NotificationStatus Status { get; private set; }
    public int Attempts { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>Subject as rendered by the last attempt (shown in the log).</summary>
    public string? Subject { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? SentAt { get; private set; }

    public static Result<NotificationMessage> Create(
        string channel, string templateKey, Guid? recipientUserId, string address, string? recipientName, string locale,
        IReadOnlyDictionary<string, string> data, bool sensitive, string deduplicationKey, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(templateKey) || string.IsNullOrWhiteSpace(deduplicationKey) || deduplicationKey.Length > 300)
        {
            return Error.Validation("notification.request_invalid");
        }

        if (string.IsNullOrWhiteSpace(address))
        {
            return Error.Validation("notification.recipient_unreachable");
        }

        return new NotificationMessage
        {
            Channel = channel,
            TemplateKey = templateKey,
            RecipientUserId = recipientUserId,
            RecipientAddress = address.Trim(),
            RecipientName = recipientName,
            Locale = locale,
            Data = new Dictionary<string, string>(data),
            Sensitive = sensitive,
            DeduplicationKey = deduplicationKey,
            Status = NotificationStatus.Pending,
            CreatedAt = now,
        };
    }

    /// <summary>
    /// Erasure of the recipient (a student's data subject request): address, name, template data and rendered subject
    /// go; the row stays as a trace that a message of this kind was sent. A pending one is given up.
    /// </summary>
    public void ErasePersonalData()
    {
        RecipientAddress = string.Empty;
        RecipientName = null;
        Data = [];
        Subject = null;
        if (Status == NotificationStatus.Pending)
        {
            Status = NotificationStatus.Failed;
            LastError = "recipient erased";
        }
    }

    public void MarkSent(string? subject, DateTimeOffset now)
    {
        Attempts++;
        Status = NotificationStatus.Sent;
        Subject = subject;
        SentAt = now;
        LastError = null;
        if (Sensitive)
        {
            // Reset links and codes are useless after delivery and must not linger in the database.
            Data = [];
        }
    }

    /// <summary>A temporary error; returns false when the attempts are exhausted and the message is now failed.</summary>
    public bool RecordTransientFailure(string error, string? subject, int maxAttempts)
    {
        Attempts++;
        Subject = subject ?? Subject;
        LastError = Truncate(error);
        if (Attempts < maxAttempts)
        {
            return true;
        }

        Status = NotificationStatus.Failed;
        return false;
    }

    public void MarkFailed(string error, string? subject)
    {
        Attempts++;
        Subject = subject ?? Subject;
        Status = NotificationStatus.Failed;
        LastError = Truncate(error);
    }

    /// <summary>An admin sends it again: back to pending with a fresh attempt budget.</summary>
    public Result<NotificationMessage> Requeue()
    {
        if (Status == NotificationStatus.Pending)
        {
            return Error.Conflict("notification.already_pending");
        }

        if (Sensitive && Status == NotificationStatus.Sent)
        {
            return Error.Conflict("notification.resend_not_allowed");
        }

        Status = NotificationStatus.Pending;
        Attempts = 0;
        LastError = null;
        return this;
    }

    private static string Truncate(string error) => error.Length > MaxErrorLength ? error[..MaxErrorLength] : error;
}

/// <summary>Kinds of due work in the dispatch queue.</summary>
public static class DispatchKinds
{
    /// <summary>Deliver <c>notification_message</c> <see cref="DispatchItem.RefId"/>.</summary>
    public const string Send = "send";

    /// <summary>24-hour reminder of session <see cref="DispatchItem.RefId"/>.</summary>
    public const string SessionReminder = "session_reminder";

    /// <summary>Second try of a welcome email whose student profile was not linked yet (user <see cref="DispatchItem.RefId"/>).</summary>
    public const string LearnerWelcome = "learner_welcome";
}

/// <summary>
/// Work due at <see cref="DueAt"/>. Infrastructure table like the outbox: no RLS, because the worker scans it across
/// tenants; it holds ids only (no personal data) and <see cref="TenantId"/> becomes the tenant context of the work.
/// One row per (kind, ref): rescheduling moves the row.
/// </summary>
public sealed class DispatchItem
{
    private DispatchItem()
    {
    }

    public DispatchItem(Guid tenantId, string kind, Guid refId, DateTimeOffset dueAt)
    {
        Id = Guid.CreateVersion7();
        TenantId = tenantId;
        Kind = kind;
        RefId = refId;
        DueAt = dueAt;
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string Kind { get; private set; } = string.Empty;
    public Guid RefId { get; private set; }
    public DateTimeOffset DueAt { get; set; }
}
