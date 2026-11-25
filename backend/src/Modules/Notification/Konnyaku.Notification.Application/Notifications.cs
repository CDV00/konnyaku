using System.Globalization;
using Konnyaku.Application.Persistence;
using Konnyaku.Application.Tenancy;
using Konnyaku.Crm.Contracts;
using Konnyaku.Identity.Contracts;
using Konnyaku.Notification.Contracts;
using Konnyaku.Notification.Domain;
using Konnyaku.Platform.Contracts;
using Konnyaku.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Konnyaku.Notification.Application;

public interface INotificationDbContext
{
    DbSet<NotificationMessage> Messages { get; }

    /// <summary>Cross-tenant due-work queue (no tenant filter): always filter by tenant yourself when reading it here.</summary>
    DbSet<DispatchItem> DispatchItems { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

public sealed class NotificationOptions
{
    /// <summary>Delivery attempts before a message is marked failed (transient errors only; permanent ones stop at once).</summary>
    public int MaxAttempts { get; set; } = 6;

    /// <summary>First retry delay; each next one is four times longer (1 min, 4, 16, 64, 256 by default), capped at 6 hours.</summary>
    public int RetryBaseDelaySeconds { get; set; } = 60;

    public int ReminderLeadHours { get; set; } = 24;

    /// <summary>Links in messages: <c>{host}</c> is the tenant's first host (e.g. <c>http://{host}:4000</c> in dev).</summary>
    public string SiteUrlTemplate { get; set; } = "https://{host}";

    public TimeSpan RetryDelay(int attempts)
    {
        var seconds = RetryBaseDelaySeconds * Math.Pow(4, Math.Max(0, attempts - 1));
        return TimeSpan.FromSeconds(Math.Min(seconds, TimeSpan.FromHours(6).TotalSeconds));
    }
}

public static class NotificationTemplates
{
    public const string LeadCreated = "lead.created";
    public const string LearnerWelcome = "learner.welcome";
    public const string CreditLow = "credit.low";
    public const string EnrollmentActivated = "enrollment.activated";
    public const string SessionRescheduled = "session.rescheduled";
    public const string SessionCancelled = "session.cancelled";
    public const string SessionReminder = "session.reminder";
    public const string PayrollLocked = "payroll.locked";
}

// ---- ports implemented in Infrastructure -------------------------------------------------------------------------

public sealed record RenderedContent(string Subject, string Html, string Text);

/// <summary>Templates per (key, channel, locale). Null when the template does not exist in any usable locale.</summary>
public interface ITemplateRenderer
{
    RenderedContent? Render(string channel, string templateKey, IReadOnlyList<string> locales, IReadOnlyDictionary<string, string> values);
}

public enum DeliveryStatus
{
    Sent,

    /// <summary>Try again later (network, 4xx, provider down, not configured yet).</summary>
    Transient,

    /// <summary>Will never work (invalid or unknown mailbox, rejected content): stop and record why.</summary>
    Permanent,
}

public sealed record DeliveryResult(DeliveryStatus Status, string? Error = null)
{
    public static readonly DeliveryResult Ok = new(DeliveryStatus.Sent);

    public static DeliveryResult Transient(string error) => new(DeliveryStatus.Transient, error);

    public static DeliveryResult Permanent(string error) => new(DeliveryStatus.Permanent, error);
}

public sealed record EmailMessage(string To, string? ToName, string? FromName, string Subject, string Html, string Text);

/// <summary>Email provider (SMTP today; an HTTP API provider can replace it).</summary>
public interface IEmailSender
{
    Task<DeliveryResult> SendAsync(EmailMessage message, CancellationToken cancellationToken);
}

public sealed record OutgoingNotification(string Address, string? RecipientName, string SenderName, RenderedContent Content);

/// <summary>A delivery channel. New channels (Zalo ZNS, push) implement this and get templates under their own name.</summary>
public interface INotificationChannel
{
    string Name { get; }

    Task<DeliveryResult> DeliverAsync(OutgoingNotification notification, CancellationToken cancellationToken);
}

public sealed class EmailChannel(IEmailSender sender) : INotificationChannel
{
    public string Name => NotificationChannels.Email;

    public Task<DeliveryResult> DeliverAsync(OutgoingNotification n, CancellationToken cancellationToken) =>
        sender.SendAsync(new EmailMessage(n.Address, n.RecipientName, n.SenderName, n.Content.Subject, n.Content.Html, n.Content.Text), cancellationToken);
}

/// <summary>Work run by the dispatcher in the tenant of its queue row. Returns when to run again, or null when done.</summary>
public interface IDispatchJob
{
    string Kind { get; }

    Task<DateTimeOffset?> RunAsync(Guid refId, CancellationToken cancellationToken);
}

// ---- helpers ------------------------------------------------------------------------------------------------------

public static class NotificationFormat
{
    public static readonly string[] SupportedLocales = ["vi", "en"];

    public static CultureInfo Culture(string locale) => CultureInfo.GetCultureInfo(locale switch
    {
        "vi" => "vi-VN",
        "en" => "en-GB",
        _ => locale,
    });

    /// <summary>"vi-VN" → "vi"; unknown → null so the caller falls back to the tenant default.</summary>
    public static string? NormalizeLocale(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale))
        {
            return null;
        }

        var language = locale.Split('-', '_')[0].Trim().ToLowerInvariant();
        return SupportedLocales.Contains(language) ? language : null;
    }

    /// <summary>Full date and time in the class/branch time zone, written the way the locale writes it.</summary>
    public static string DateTime(DateTimeOffset at, string timeZone, string locale)
    {
        var zone = FindZone(timeZone);
        var local = zone is null ? at : TimeZoneInfo.ConvertTime(at, zone);
        return local.ToString("f", Culture(locale));
    }

    public static string Date(DateOnly date, string locale) => date.ToString("D", Culture(locale));

    /// <summary>Numeric date (01/10/2026), for subjects and ranges.</summary>
    public static string ShortDate(DateOnly date, string locale) => date.ToString("d", Culture(locale));

    public static string Number(decimal value, string locale) => value.ToString("0.##", Culture(locale));

    private static TimeZoneInfo? FindZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }
    }
}

/// <summary>A reachable recipient. <see cref="Key"/> identifies the person in deduplication keys.</summary>
public sealed record Recipient(string Key, Guid? UserId, string Address, string? Name, string? Locale);

/// <summary>Address, name and locale of people, read through other modules' contracts (no cross-schema reads).</summary>
public sealed class Recipients(IIdentityApi identity, ICrmContactsApi crm)
{
    public async Task<Recipient?> UserAsync(Guid userId, CancellationToken ct)
    {
        var member = await identity.GetMemberAsync(userId, ct);
        return string.IsNullOrWhiteSpace(member?.Email)
            ? null
            : new Recipient($"u:{userId}", userId, member.Email, member.DisplayName, member.PreferredLocale);
    }

    /// <summary>The student's contact email, else the email of the linked account. Locale: the student's communication locale.</summary>
    public async Task<Recipient?> StudentAsync(Guid studentId, CancellationToken ct)
    {
        var contact = await crm.GetStudentContactAsync(studentId, ct);
        return contact is null ? null : await FromContactAsync(contact, ct);
    }

    public async Task<Recipient?> FromContactAsync(StudentContactDto contact, CancellationToken ct)
    {
        var address = contact.Email;
        if (string.IsNullOrWhiteSpace(address) && contact.UserId is { } userId)
        {
            address = (await identity.GetMemberAsync(userId, ct))?.Email;
        }

        return string.IsNullOrWhiteSpace(address)
            ? null
            : new Recipient($"s:{contact.StudentId}", contact.UserId, address, contact.FullName, contact.CommunicationLocale);
    }
}

/// <summary>Tenant branding and defaults for messages.</summary>
public sealed class TenantBranding(IPlatformApi platform, ITenantContext tenant, NotificationOptions options)
{
    private TenantInfo? _info;

    public async Task<TenantInfo?> InfoAsync(CancellationToken ct) =>
        _info ??= await platform.GetTenantAsync(tenant.RequiredTenantId, ct);

    public async Task<string> DefaultLocaleAsync(CancellationToken ct) =>
        NotificationFormat.NormalizeLocale((await InfoAsync(ct))?.DefaultLocale) ?? "vi";

    public async Task<string> TimeZoneAsync(CancellationToken ct) => (await InfoAsync(ct))?.TimeZone ?? "Asia/Ho_Chi_Minh";

    public async Task<string> SiteUrlAsync(CancellationToken ct)
    {
        var host = (await InfoAsync(ct))?.Hosts.FirstOrDefault();
        return host is null ? string.Empty : options.SiteUrlTemplate.Replace("{host}", host, StringComparison.Ordinal).TrimEnd('/');
    }
}

// ---- queue --------------------------------------------------------------------------------------------------------

/// <summary>
/// Queues messages of the current tenant. Consumers call <see cref="AddAsync"/> per recipient then <see cref="SaveAsync"/>;
/// other modules use <see cref="INotificationApi"/>.
/// </summary>
public sealed class NotificationQueue(
    INotificationDbContext db, ITenantContext tenant, Recipients recipients, TenantBranding branding, TimeProvider clock,
    IDbErrorClassifier errors) : INotificationApi
{
    /// <summary>Adds a message unless its deduplication key was already used. Returns false for a duplicate.</summary>
    public async Task<bool> AddAsync(
        string templateKey, Recipient recipient, IReadOnlyDictionary<string, string> data, string deduplicationKey, CancellationToken ct,
        string channel = NotificationChannels.Email, bool sensitive = false, DateTimeOffset? notBefore = null)
    {
        if (db.Messages.Local.Any(m => m.DeduplicationKey == deduplicationKey) ||
            await db.Messages.AnyAsync(m => m.DeduplicationKey == deduplicationKey, ct))
        {
            return false;
        }

        var locale = NotificationFormat.NormalizeLocale(recipient.Locale) ?? await branding.DefaultLocaleAsync(ct);
        var now = clock.GetUtcNow();
        var created = NotificationMessage.Create(channel, templateKey, recipient.UserId, recipient.Address, recipient.Name, locale, data,
            sensitive, deduplicationKey, now);
        if (!created.IsSuccess)
        {
            return false;
        }

        db.Messages.Add(created.Value);
        db.DispatchItems.Add(new DispatchItem(tenant.RequiredTenantId, DispatchKinds.Send, created.Value.Id, notBefore ?? now));
        return true;
    }

    /// <summary>
    /// Saves queued messages. A concurrent delivery of the same event can insert the same key first: the unique index
    /// rejects ours and we throw so the outbox retries — the retry sees the key and skips it.
    /// </summary>
    public async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (errors.IsUniqueViolation(ex))
        {
            throw new InvalidOperationException("Notification queued concurrently; retry.", ex);
        }
    }

    public async Task<Result<NotificationQueued>> EnqueueAsync(NotificationRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.TemplateKey) || string.IsNullOrWhiteSpace(request.DeduplicationKey))
        {
            return Error.Validation("notification.request_invalid");
        }

        var existing = await db.Messages.AsNoTracking().Where(m => m.DeduplicationKey == request.DeduplicationKey)
            .Select(m => (Guid?)m.Id).FirstOrDefaultAsync(cancellationToken);
        if (existing is { } existingId)
        {
            return new NotificationQueued(existingId, true);
        }

        var recipient = await ResolveAsync(request.Recipient, cancellationToken);
        if (recipient is null)
        {
            return Error.Validation("notification.recipient_unreachable");
        }

        if (!await AddAsync(request.TemplateKey, recipient, request.Data ?? new Dictionary<string, string>(), request.DeduplicationKey,
                cancellationToken, request.Channel ?? NotificationChannels.Email, request.Sensitive, request.NotBefore))
        {
            return Error.Validation("notification.request_invalid");
        }

        var id = db.Messages.Local.First(m => m.DeduplicationKey == request.DeduplicationKey).Id;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return new NotificationQueued(id, false);
        }
        catch (DbUpdateException ex) when (errors.IsUniqueViolation(ex))
        {
            var winner = await db.Messages.AsNoTracking().Where(m => m.DeduplicationKey == request.DeduplicationKey)
                .Select(m => m.Id).FirstAsync(cancellationToken);
            return new NotificationQueued(winner, true);
        }
    }

    private async Task<Recipient?> ResolveAsync(NotificationRecipient requested, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(requested.Address))
        {
            var key = requested.UserId is { } id ? $"u:{id}" : $"a:{requested.Address.Trim().ToLowerInvariant()}";
            return new Recipient(key, requested.UserId, requested.Address, requested.DisplayName, requested.Locale);
        }

        if (requested.UserId is not { } userId || await recipients.UserAsync(userId, ct) is not { } user)
        {
            return null;
        }

        return user with
        {
            Name = requested.DisplayName ?? user.Name,
            Locale = requested.Locale ?? user.Locale,
        };
    }
}

/// <summary>Places, moves and removes due work of the current tenant (one row per kind and reference).</summary>
public sealed class DispatchSchedule(INotificationDbContext db, ITenantContext tenant)
{
    public async Task ScheduleAsync(string kind, Guid refId, DateTimeOffset dueAt, CancellationToken ct)
    {
        var tenantId = tenant.RequiredTenantId;
        var item = await db.DispatchItems.FirstOrDefaultAsync(d => d.Kind == kind && d.RefId == refId && d.TenantId == tenantId, ct);
        if (item is null)
        {
            db.DispatchItems.Add(new DispatchItem(tenantId, kind, refId, dueAt));
        }
        else
        {
            item.DueAt = dueAt;
        }
    }

    public async Task CancelAsync(string kind, Guid refId, CancellationToken ct)
    {
        var tenantId = tenant.RequiredTenantId;
        var item = await db.DispatchItems.FirstOrDefaultAsync(d => d.Kind == kind && d.RefId == refId && d.TenantId == tenantId, ct);
        if (item is not null)
        {
            db.DispatchItems.Remove(item);
        }
    }
}

// ---- delivery -----------------------------------------------------------------------------------------------------

/// <summary>Renders and delivers one queued message; retries transient errors with backoff, stops on permanent ones.</summary>
public sealed class SendJob(
    INotificationDbContext db, ITemplateRenderer renderer, IEnumerable<INotificationChannel> channels, TenantBranding branding,
    NotificationOptions options, TimeProvider clock) : IDispatchJob
{
    public string Kind => DispatchKinds.Send;

    public async Task<DateTimeOffset?> RunAsync(Guid refId, CancellationToken cancellationToken)
    {
        var message = await db.Messages.FirstOrDefaultAsync(m => m.Id == refId, cancellationToken);
        if (message is not { Status: NotificationStatus.Pending })
        {
            return null;
        }

        var info = await branding.InfoAsync(cancellationToken);
        var values = new Dictionary<string, string>(message.Data)
        {
            ["tenantName"] = info?.Name ?? string.Empty,
            ["siteUrl"] = await branding.SiteUrlAsync(cancellationToken),
            ["recipientName"] = message.RecipientName ?? string.Empty,
        };
        var locales = new[] { message.Locale, await branding.DefaultLocaleAsync(cancellationToken), "vi" }.Distinct().ToList();
        var content = renderer.Render(message.Channel, message.TemplateKey, locales, values);
        var channel = channels.FirstOrDefault(c => c.Name == message.Channel);

        DateTimeOffset? retryAt = null;
        if (content is null)
        {
            message.MarkFailed($"template_missing: {message.Channel}/{message.TemplateKey}/{message.Locale}", null);
        }
        else if (channel is null)
        {
            message.MarkFailed($"channel_unavailable: {message.Channel}", content.Subject);
        }
        else
        {
            var result = await channel.DeliverAsync(new OutgoingNotification(message.RecipientAddress, message.RecipientName,
                info?.Name ?? string.Empty, content), cancellationToken);
            switch (result.Status)
            {
                case DeliveryStatus.Sent:
                    message.MarkSent(content.Subject, clock.GetUtcNow());
                    break;
                case DeliveryStatus.Permanent:
                    message.MarkFailed(result.Error ?? "permanent", content.Subject);
                    break;
                default:
                    if (message.RecordTransientFailure(result.Error ?? "transient", content.Subject, options.MaxAttempts))
                    {
                        retryAt = clock.GetUtcNow() + options.RetryDelay(message.Attempts);
                    }

                    break;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return retryAt;
    }
}

// ---- admin log ----------------------------------------------------------------------------------------------------

public sealed record NotificationView(
    Guid Id, DateTimeOffset CreatedAt, string TemplateKey, string Channel, string RecipientAddress, string? RecipientName, string Locale,
    string Status, int Attempts, string? LastError, string? Subject, DateTimeOffset? SentAt, bool Sensitive);

public sealed class NotificationLog(INotificationDbContext db, DispatchSchedule schedule, TimeProvider clock)
{
    public async Task<IReadOnlyList<NotificationView>> ListAsync(string? status, int? take, CancellationToken ct)
    {
        var query = db.Messages.AsNoTracking();
        if (Enum.TryParse<NotificationStatus>(status, ignoreCase: true, out var parsed))
        {
            query = query.Where(m => m.Status == parsed);
        }

        return await query.OrderByDescending(m => m.CreatedAt).Take(Math.Clamp(take ?? 200, 1, 500))
            .Select(m => new NotificationView(m.Id, m.CreatedAt, m.TemplateKey, m.Channel, m.RecipientAddress, m.RecipientName, m.Locale,
                m.Status.ToString(), m.Attempts, m.LastError, m.Subject, m.SentAt, m.Sensitive))
            .ToListAsync(ct);
    }

    public async Task<Result<bool>> ResendAsync(Guid id, CancellationToken ct)
    {
        var message = await db.Messages.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (message is null)
        {
            return Error.NotFound("notification.message_not_found");
        }

        var requeued = message.Requeue();
        if (!requeued.IsSuccess)
        {
            return requeued.Error!;
        }

        await schedule.ScheduleAsync(DispatchKinds.Send, message.Id, clock.GetUtcNow(), ct);
        await db.SaveChangesAsync(ct);
        return true;
    }
}
