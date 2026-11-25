using Konnyaku.SharedKernel;

namespace Konnyaku.Notification.Domain;

/// <summary>
/// One notification in a user's in-app inbox. The title is an i18n code (<see cref="TitleKey"/> + <see cref="Params"/>),
/// translated by the web app like error codes, so the same item reads right in Vietnamese and English.
/// <see cref="Seq"/> grows with every item that becomes visible (from one sequence for every tenant): clients keep the
/// highest one they saw as their sync cursor. An item can be created ahead of time (a score released later): it stays
/// hidden until <see cref="VisibleAt"/>, and gets a fresh <see cref="Seq"/> when it shows.
/// </summary>
public sealed class InboxItem : Entity, ITenantOwned
{
    public const int MaxKeyLength = 300;

    private InboxItem()
    {
    }

    public Guid TenantId { get; private set; }
    public Guid UserId { get; private set; }
    public string Kind { get; private set; } = string.Empty;
    public string TitleKey { get; private set; } = string.Empty;
    public Dictionary<string, string> Params { get; private set; } = [];

    /// <summary>Path in the web app (e.g. <c>/learn/schedule</c>), never an outside URL.</summary>
    public string? Link { get; private set; }

    public string DeduplicationKey { get; private set; } = string.Empty;
    public long Seq { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset VisibleAt { get; private set; }
    public DateTimeOffset? ReadAt { get; private set; }

    public static Result<InboxItem> Create(
        Guid userId, string kind, string titleKey, IReadOnlyDictionary<string, string> parameters, string? link, string deduplicationKey,
        DateTimeOffset now, DateTimeOffset? visibleAt = null)
    {
        if (userId == Guid.Empty || string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(titleKey) ||
            string.IsNullOrWhiteSpace(deduplicationKey) || deduplicationKey.Length > MaxKeyLength)
        {
            return Error.Validation("notification.request_invalid");
        }

        if (link is not null && (!link.StartsWith('/') || link.StartsWith("//", StringComparison.Ordinal)))
        {
            return Error.Validation("notification.link_invalid");
        }

        return new InboxItem
        {
            UserId = userId,
            Kind = kind,
            TitleKey = titleKey,
            Params = new Dictionary<string, string>(parameters),
            Link = link,
            DeduplicationKey = deduplicationKey,
            CreatedAt = now,
            VisibleAt = visibleAt is { } at && at > now ? at : now,
        };
    }

    public bool IsVisible(DateTimeOffset now) => VisibleAt <= now;

    /// <summary>Idempotent: reading twice keeps the first time.</summary>
    public void MarkRead(DateTimeOffset now) => ReadAt ??= now;

    /// <summary>Erasure of the recipient: what the item said goes, the row stays (read state, counts).</summary>
    public void ErasePersonalData() => Params = [];
}

/// <summary>
/// A user's choice of extra channels for one kind of notification. No row means the defaults (email and push on).
/// The in-app inbox is always on.
/// </summary>
public sealed class NotificationPreference : Entity, ITenantOwned
{
    private NotificationPreference()
    {
    }

    public NotificationPreference(Guid userId, string kind, bool email, bool push, DateTimeOffset now)
    {
        UserId = userId;
        Kind = kind;
        Set(email, push, now);
    }

    public Guid TenantId { get; private set; }
    public Guid UserId { get; private set; }
    public string Kind { get; private set; } = string.Empty;
    public bool Email { get; private set; }
    public bool Push { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public void Set(bool email, bool push, DateTimeOffset now)
    {
        Email = email;
        Push = push;
        UpdatedAt = now;
    }
}

/// <summary>
/// One browser that accepted Web Push for a user. Personal data: deleted on sign-out from that browser, when the push
/// service says it is gone (404/410), and on erasure of the user.
/// </summary>
public sealed class PushSubscription : Entity, ITenantOwned
{
    public const int MaxEndpointLength = 1000;

    private PushSubscription()
    {
    }

    public Guid TenantId { get; private set; }
    public Guid UserId { get; private set; }
    public string Endpoint { get; private set; } = string.Empty;
    public string P256dh { get; private set; } = string.Empty;
    public string Auth { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? LastSuccessAt { get; private set; }

    public static Result<PushSubscription> Create(Guid userId, string endpoint, string p256dh, string auth, DateTimeOffset now)
    {
        if (!IsValid(endpoint, p256dh, auth))
        {
            return Error.Validation("notification.push_subscription_invalid");
        }

        return new PushSubscription { UserId = userId, Endpoint = endpoint, P256dh = p256dh, Auth = auth, CreatedAt = now };
    }

    /// <summary>The same browser subscribed again (possibly signed in as someone else): it now belongs to that user.</summary>
    public Result<PushSubscription> Renew(Guid userId, string p256dh, string auth, DateTimeOffset now)
    {
        if (!IsValid(Endpoint, p256dh, auth))
        {
            return Error.Validation("notification.push_subscription_invalid");
        }

        UserId = userId;
        P256dh = p256dh;
        Auth = auth;
        CreatedAt = now;
        return this;
    }

    public void Delivered(DateTimeOffset now) => LastSuccessAt = now;

    /// <summary>Push services are HTTPS endpoints; keys are base64url (P-256 point, 16-byte secret).</summary>
    private static bool IsValid(string endpoint, string p256dh, string auth) =>
        !string.IsNullOrWhiteSpace(endpoint) && endpoint.Length <= MaxEndpointLength &&
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        !string.IsNullOrWhiteSpace(p256dh) && p256dh.Length <= 200 && !string.IsNullOrWhiteSpace(auth) && auth.Length <= 100;
}
