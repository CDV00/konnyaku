using Konnyaku.SharedKernel;

namespace Konnyaku.Notification.Contracts;

/// <summary>Delivery channels. Only <see cref="Email"/> is wired today; Zalo ZNS and push plug in as new channels.</summary>
public static class NotificationChannels
{
    public const string Email = "Email";
}

/// <summary>
/// Who receives a notification. Either a user of the current tenant (<see cref="UserId"/>: address, name and locale are
/// read from Identity) or a direct <see cref="Address"/>. A given <see cref="Locale"/> wins over the user's preference.
/// </summary>
public sealed record NotificationRecipient(Guid? UserId, string? Address, string? DisplayName, string? Locale);

/// <summary>
/// A message to send with the template <see cref="TemplateKey"/> (e.g. <c>identity.password_reset</c>). <see cref="Data"/>
/// fills the template placeholders; the tenant name and site URL are added by the module. The same
/// <see cref="DeduplicationKey"/> is never sent twice in a tenant. <see cref="Sensitive"/> data (reset links, codes) is
/// erased once sent and never shown in the log.
/// </summary>
public sealed record NotificationRequest(
    string TemplateKey,
    NotificationRecipient Recipient,
    IReadOnlyDictionary<string, string> Data,
    string DeduplicationKey,
    string Channel = NotificationChannels.Email,
    bool Sensitive = false,
    DateTimeOffset? NotBefore = null);

/// <summary>Queued notification; <see cref="Duplicate"/> when the deduplication key was already used.</summary>
public sealed record NotificationQueued(Guid NotificationId, bool Duplicate);

/// <summary>Synchronous entry point for other modules: queues a notification of the current tenant (own transaction).</summary>
public interface INotificationApi
{
    Task<Result<NotificationQueued>> EnqueueAsync(NotificationRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Asynchronous entry point: publish from your own outbox when the notification must be atomic with your own change
/// (e.g. a password reset token). Notification queues it exactly like <see cref="INotificationApi.EnqueueAsync"/>.
/// </summary>
public sealed record NotificationRequestedV1(
    string TemplateKey,
    Guid? RecipientUserId,
    string? RecipientAddress,
    string? RecipientName,
    string? Locale,
    IReadOnlyDictionary<string, string> Data,
    string DeduplicationKey,
    string Channel,
    bool Sensitive) : IIntegrationEvent;
