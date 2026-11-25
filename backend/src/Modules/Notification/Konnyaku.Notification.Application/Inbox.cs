using Konnyaku.Application.Tenancy;
using Konnyaku.Notification.Domain;
using Konnyaku.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Konnyaku.Notification.Application;

/// <summary>
/// Kinds of notification. A kind is also the email template key when it has one (<see cref="WithEmail"/>) and the i18n
/// code of the inbox title (<c>inbox.titles.&lt;kind with _&gt;</c> in the web app).
/// </summary>
public static class InboxKinds
{
    public const string SessionRescheduled = NotificationTemplates.SessionRescheduled;
    public const string SessionCancelled = NotificationTemplates.SessionCancelled;
    public const string SessionReminder = NotificationTemplates.SessionReminder;
    public const string CreditLow = NotificationTemplates.CreditLow;
    public const string EnrollmentActivated = NotificationTemplates.EnrollmentActivated;
    public const string LeadCreated = NotificationTemplates.LeadCreated;
    public const string PayrollLocked = NotificationTemplates.PayrollLocked;
    public const string WaitlistOffered = "booking.waitlist_offered";
    public const string MakeupApproved = "booking.makeup_approved";
    public const string AssessmentAssigned = "assessment.assigned";
    public const string AssessmentDueSoon = "assessment.due_soon";
    public const string AssessmentGraded = "assessment.graded";

    /// <summary>Kinds a learner can receive (the learner area's settings list them in this order).</summary>
    public static readonly IReadOnlyList<string> Learner =
    [
        SessionRescheduled, SessionCancelled, SessionReminder, WaitlistOffered, MakeupApproved, AssessmentAssigned, AssessmentDueSoon,
        AssessmentGraded, EnrollmentActivated, CreditLow,
    ];

    /// <summary>Kinds a staff member can receive (teachers: sessions and payroll; consultants and admins: leads).</summary>
    public static readonly IReadOnlyList<string> Staff = [SessionRescheduled, SessionCancelled, SessionReminder, LeadCreated, PayrollLocked];

    /// <summary>Kinds that also have an email template; the others reach the inbox and Web Push only.</summary>
    public static readonly IReadOnlySet<string> WithEmail = new HashSet<string>(StringComparer.Ordinal)
    {
        SessionRescheduled, SessionCancelled, SessionReminder, CreditLow, EnrollmentActivated, LeadCreated, PayrollLocked,
    };

    public static IReadOnlyList<string> For(bool staff) => staff ? Staff : Learner;
}

// ---- ports implemented in Infrastructure -------------------------------------------------------------------------

/// <summary>
/// "Something new" for one user, pushed through Redis to the realtime service and on to that user's open tabs. Ids only:
/// the browser fetches the content through the API, which checks access as usual.
/// </summary>
public sealed record RealtimeSignal(string Type, Guid? Id, long Seq);

public static class RealtimeSignalTypes
{
    public const string Inbox = "inbox";
    public const string InboxRead = "inbox.read";
}

public interface IRealtimePublisher
{
    /// <summary>Best effort: a lost signal only delays the update until the next sync (reconnect or fallback poll).</summary>
    Task PublishAsync(Guid userId, RealtimeSignal signal, CancellationToken cancellationToken);
}

public enum PushOutcome
{
    Delivered,

    /// <summary>Try again later (push service down, rate limited).</summary>
    Transient,

    /// <summary>The browser unsubscribed or the subscription expired (404/410): delete it.</summary>
    Gone,

    /// <summary>Rejected for good (bad keys, too big): keep the row, nothing to retry.</summary>
    Rejected,
}

public sealed record PushTarget(string Endpoint, string P256dh, string Auth);

/// <summary>Web Push (VAPID). Sends a push without payload: the service worker fetches what to show.</summary>
public interface IPushSender
{
    bool IsConfigured { get; }

    /// <summary>Base64url application server key for <c>pushManager.subscribe</c>; null when push is not configured.</summary>
    string? PublicKey { get; }

    Task<PushOutcome> SendAsync(PushTarget target, CancellationToken cancellationToken);
}

// ---- routing ------------------------------------------------------------------------------------------------------

/// <summary>
/// One notification for one person. <see cref="Params"/> go to the inbox (raw values: ISO dates, numbers; the web app
/// formats them in the reader's language); <see cref="EmailData"/> fills the email template (null: no email for this
/// kind). <see cref="VisibleAt"/> holds the inbox item back until then (a score released later).
/// </summary>
public sealed record Notice(
    string Kind,
    Recipient Recipient,
    string DeduplicationKey,
    string? Link,
    IReadOnlyDictionary<string, string> Params,
    IReadOnlyDictionary<string, string>? EmailData = null,
    DateTimeOffset? VisibleAt = null);

public sealed record ChannelChoice(bool Email, bool Push)
{
    public static readonly ChannelChoice Defaults = new(true, true);
}

/// <summary>Channel preferences of users of the current tenant (cached for the scope).</summary>
public sealed class NotificationPreferences(INotificationDbContext db)
{
    private readonly Dictionary<Guid, Dictionary<string, ChannelChoice>> _cache = [];

    public async Task<ChannelChoice> ForAsync(Guid userId, string kind, CancellationToken ct)
    {
        if (!_cache.TryGetValue(userId, out var choices))
        {
            choices = await db.Preferences.AsNoTracking().Where(p => p.UserId == userId)
                .ToDictionaryAsync(p => p.Kind, p => new ChannelChoice(p.Email, p.Push), StringComparer.Ordinal, ct);
            _cache[userId] = choices;
        }

        return choices.GetValueOrDefault(kind) ?? ChannelChoice.Defaults;
    }
}

/// <summary>
/// The channel router: every notice goes to the inbox of its user (when the person has an account), plus email and
/// Web Push as the person chose. Emails of the same kind are grouped (see <see cref="SendJob"/>). Call
/// <see cref="SaveAsync"/> once after routing; it commits, then signals the open tabs.
/// </summary>
public sealed class NotificationRouter(
    INotificationDbContext db, NotificationQueue queue, NotificationPreferences preferences, ITenantContext tenant, TimeProvider clock,
    IRealtimePublisher realtime, ILogger<NotificationRouter> logger)
{
    private readonly List<InboxItem> _toSignal = [];

    public async Task RouteAsync(Notice notice, CancellationToken ct)
    {
        var choice = notice.Recipient.UserId is { } id ? await preferences.ForAsync(id, notice.Kind, ct) : ChannelChoice.Defaults;
        if (notice.Recipient.UserId is { } userId)
        {
            await AddInboxAsync(userId, notice, choice.Push, ct);
        }

        if (notice.EmailData is { } data && choice.Email && InboxKinds.WithEmail.Contains(notice.Kind) &&
            !string.IsNullOrWhiteSpace(notice.Recipient.Address))
        {
            var values = new Dictionary<string, string>(data);
            if (Zone(notice.Link) is { } zone)
            {
                values["inboxPath"] = $"{zone}/inbox";
            }

            await queue.AddAsync(notice.Kind, notice.Recipient, values, notice.DeduplicationKey, ct,
                digestKey: $"{notice.Recipient.Key}:{notice.Kind}");
        }
    }

    public async Task SaveAsync(CancellationToken ct)
    {
        await queue.SaveAsync(ct);
        var signals = _toSignal.ToList();
        _toSignal.Clear();
        foreach (var item in signals)
        {
            await SignalAsync(realtime, logger, item, ct);
        }
    }

    /// <summary>Tells the user's open tabs (all realtime replicas) that an inbox item arrived.</summary>
    public static async Task SignalAsync(IRealtimePublisher realtime, ILogger logger, InboxItem item, CancellationToken ct)
    {
        try
        {
            await realtime.PublishAsync(item.UserId, new RealtimeSignal(RealtimeSignalTypes.Inbox, item.Id, item.Seq), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Realtime signal for inbox item {ItemId} not published", item.Id);
        }
    }

    /// <summary>"/learn/schedule" → "/learn".</summary>
    public static string? Zone(string? link)
    {
        if (string.IsNullOrEmpty(link) || !link.StartsWith('/'))
        {
            return null;
        }

        var end = link.IndexOf('/', 1);
        return end < 0 ? link : link[..end];
    }

    private async Task AddInboxAsync(Guid userId, Notice notice, bool push, CancellationToken ct)
    {
        if (db.InboxItems.Local.Any(i => i.DeduplicationKey == notice.DeduplicationKey) ||
            await db.InboxItems.AnyAsync(i => i.DeduplicationKey == notice.DeduplicationKey, ct))
        {
            return;
        }

        var now = clock.GetUtcNow();
        var created = InboxItem.Create(userId, notice.Kind, notice.Kind, notice.Params, notice.Link, notice.DeduplicationKey, now, notice.VisibleAt);
        if (!created.IsSuccess)
        {
            logger.LogWarning("Inbox item {Kind} not created: {Code}", notice.Kind, created.Error!.Code);
            return;
        }

        var item = created.Value;
        db.InboxItems.Add(item);
        if (!item.IsVisible(now))
        {
            db.DispatchItems.Add(new DispatchItem(tenant.RequiredTenantId, DispatchKinds.InboxVisible, item.Id, item.VisibleAt));
            return;
        }

        _toSignal.Add(item);
        if (push && await db.PushSubscriptions.AnyAsync(s => s.UserId == userId, ct))
        {
            db.DispatchItems.Add(new DispatchItem(tenant.RequiredTenantId, DispatchKinds.Push, item.Id, now));
        }
    }
}

// ---- jobs ---------------------------------------------------------------------------------------------------------

/// <summary>Web Push of one inbox item to every browser of its user; gone subscriptions are deleted.</summary>
public sealed class PushJob(INotificationDbContext db, IPushSender sender, TimeProvider clock, ILogger<PushJob> logger) : IDispatchJob
{
    /// <summary>A push about something older than this is no longer worth showing.</summary>
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromMinutes(30);

    public string Kind => DispatchKinds.Push;

    public async Task<DateTimeOffset?> RunAsync(Guid refId, CancellationToken cancellationToken)
    {
        var item = await db.InboxItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == refId, cancellationToken);
        var now = clock.GetUtcNow();
        if (item is null || item.ReadAt is not null || !sender.IsConfigured || now - item.VisibleAt > GiveUpAfter)
        {
            return null;
        }

        var retry = false;
        foreach (var subscription in await db.PushSubscriptions.Where(s => s.UserId == item.UserId).ToListAsync(cancellationToken))
        {
            switch (await sender.SendAsync(new PushTarget(subscription.Endpoint, subscription.P256dh, subscription.Auth), cancellationToken))
            {
                case PushOutcome.Delivered:
                    subscription.Delivered(now);
                    break;
                case PushOutcome.Gone:
                    db.PushSubscriptions.Remove(subscription);
                    logger.LogInformation("Push subscription {SubscriptionId} is gone; deleted", subscription.Id);
                    break;
                case PushOutcome.Transient:
                    retry = true;
                    break;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return retry ? now + TimeSpan.FromMinutes(1) : null;
    }
}

/// <summary>An inbox item created ahead of time is due: it gets a fresh sequence number, then the tabs and push hear of it.</summary>
public sealed class InboxVisibleJob(
    INotificationDbContext db, NotificationPreferences preferences, IRealtimePublisher realtime, ITenantContext tenant, TimeProvider clock,
    ILogger<InboxVisibleJob> logger) : IDispatchJob
{
    public string Kind => DispatchKinds.InboxVisible;

    public async Task<DateTimeOffset?> RunAsync(Guid refId, CancellationToken cancellationToken)
    {
        var item = await db.InboxItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == refId, cancellationToken);
        var now = clock.GetUtcNow();
        if (item is null)
        {
            return null;
        }

        if (!item.IsVisible(now))
        {
            return item.VisibleAt;
        }

        var seq = await db.RenumberInboxItemAsync(item.Id, cancellationToken);
        if ((await preferences.ForAsync(item.UserId, item.Kind, cancellationToken)).Push &&
            await db.PushSubscriptions.AnyAsync(s => s.UserId == item.UserId, cancellationToken))
        {
            db.DispatchItems.Add(new DispatchItem(tenant.RequiredTenantId, DispatchKinds.Push, item.Id, now));
            await db.SaveChangesAsync(cancellationToken);
        }

        try
        {
            await realtime.PublishAsync(item.UserId, new RealtimeSignal(RealtimeSignalTypes.Inbox, item.Id, seq), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Realtime signal for inbox item {ItemId} not published", item.Id);
        }

        return null;
    }
}

// ---- the user's own inbox, settings and devices -------------------------------------------------------------------

public sealed record InboxItemView(
    Guid Id, string Kind, string TitleKey, IReadOnlyDictionary<string, string> Params, string? Link, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt,
    long Seq);

public sealed record InboxPage(IReadOnlyList<InboxItemView> Items, int Total, int Unread, long Cursor);

public sealed record InboxCount(int Unread, long Cursor);

/// <summary>Items after the client's cursor (oldest first); <see cref="More"/> when the page was full.</summary>
public sealed record InboxSync(IReadOnlyList<InboxItemView> Items, int Unread, long Cursor, bool More);

public sealed record PreferenceView(string Kind, bool Email, bool Push, bool EmailAvailable);

public sealed record PreferenceCommand(string Kind, bool Email, bool Push);

public sealed record PushKeys(string P256dh, string Auth);

public sealed record PushSubscribeCommand(string Endpoint, PushKeys Keys);

public sealed record PushSubscriptionView(Guid Id);

public sealed record PushConfigView(bool Enabled, string? PublicKey);

/// <summary>Everything a signed-in person does with their own notifications. Every query is scoped to that user.</summary>
public sealed class InboxHandlers(
    INotificationDbContext db, IRealtimePublisher realtime, IPushSender push, TimeProvider clock, ILogger<InboxHandlers> logger)
{
    public const int MaxPageSize = 50;
    public const int SyncPageSize = 100;

    public async Task<InboxPage> ListAsync(Guid userId, int? page, int? size, bool unreadOnly, CancellationToken ct)
    {
        var take = Math.Clamp(size ?? 20, 1, MaxPageSize);
        var skip = (Math.Max(page ?? 1, 1) - 1) * take;
        var visible = Visible(userId);
        var query = unreadOnly ? visible.Where(i => i.ReadAt == null) : visible;
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(i => i.Seq).Skip(skip).Take(take).Select(View).ToListAsync(ct);
        var (unread, cursor) = await CountsAsync(userId, ct);
        return new InboxPage(items, total, unread, cursor);
    }

    public async Task<InboxCount> CountAsync(Guid userId, CancellationToken ct)
    {
        var (unread, cursor) = await CountsAsync(userId, ct);
        return new InboxCount(unread, cursor);
    }

    public async Task<InboxSync> SyncAsync(Guid userId, long since, CancellationToken ct)
    {
        var items = await Visible(userId).Where(i => i.Seq > since).OrderBy(i => i.Seq).Take(SyncPageSize + 1).Select(View).ToListAsync(ct);
        var more = items.Count > SyncPageSize;
        if (more)
        {
            items.RemoveAt(items.Count - 1);
        }

        var (unread, cursor) = await CountsAsync(userId, ct);
        return new InboxSync(items, unread, more ? items[^1].Seq : Math.Max(cursor, since), more);
    }

    public async Task<Result<InboxCount>> MarkReadAsync(Guid userId, Guid itemId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var item = await db.InboxItems.FirstOrDefaultAsync(i => i.Id == itemId && i.UserId == userId && i.VisibleAt <= now, ct);
        if (item is null)
        {
            return Error.NotFound("notification.inbox_item_not_found");
        }

        if (item.ReadAt is null)
        {
            item.MarkRead(now);
            await db.SaveChangesAsync(ct);
            await SignalReadAsync(userId, itemId, ct);
        }

        return await CountAsync(userId, ct);
    }

    public async Task<InboxCount> MarkAllReadAsync(Guid userId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var changed = await db.InboxItems.Where(i => i.UserId == userId && i.ReadAt == null && i.VisibleAt <= now)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.ReadAt, now), ct);
        if (changed > 0)
        {
            await SignalReadAsync(userId, null, ct);
        }

        return await CountAsync(userId, ct);
    }

    public async Task<IReadOnlyList<PreferenceView>> PreferencesAsync(Guid userId, bool staff, CancellationToken ct)
    {
        var rows = await db.Preferences.AsNoTracking().Where(p => p.UserId == userId).ToDictionaryAsync(p => p.Kind, StringComparer.Ordinal, ct);
        return InboxKinds.For(staff).Select(kind =>
        {
            var row = rows.GetValueOrDefault(kind);
            var email = InboxKinds.WithEmail.Contains(kind);
            return new PreferenceView(kind, email && (row?.Email ?? true), row?.Push ?? true, email);
        }).ToList();
    }

    public async Task<Result<IReadOnlyList<PreferenceView>>> SetPreferencesAsync(
        Guid userId, bool staff, IReadOnlyList<PreferenceCommand>? commands, CancellationToken ct)
    {
        var kinds = InboxKinds.For(staff);
        if (commands is null || commands.Count == 0 || commands.Any(c => !kinds.Contains(c.Kind)) ||
            commands.Select(c => c.Kind).Distinct().Count() != commands.Count)
        {
            return Error.Validation("notification.preferences_invalid");
        }

        var now = clock.GetUtcNow();
        var rows = await db.Preferences.Where(p => p.UserId == userId).ToDictionaryAsync(p => p.Kind, StringComparer.Ordinal, ct);
        foreach (var command in commands)
        {
            // Kinds without an email template keep email "on": the choice means nothing for them.
            var email = command.Email || !InboxKinds.WithEmail.Contains(command.Kind);
            if (rows.TryGetValue(command.Kind, out var row))
            {
                row.Set(email, command.Push, now);
            }
            else
            {
                db.Preferences.Add(new NotificationPreference(userId, command.Kind, email, command.Push, now));
            }
        }

        await db.SaveChangesAsync(ct);
        return (await PreferencesAsync(userId, staff, ct)).ToList();
    }

    public PushConfigView PushConfig() => new(push.IsConfigured, push.PublicKey);

    /// <summary>Adds this browser, or hands it over when it was subscribed for someone else of the tenant.</summary>
    public async Task<Result<PushSubscriptionView>> SubscribeAsync(Guid userId, PushSubscribeCommand? command, CancellationToken ct)
    {
        if (!push.IsConfigured)
        {
            return Error.Conflict("notification.push_not_configured");
        }

        if (command?.Keys is null || string.IsNullOrWhiteSpace(command.Endpoint))
        {
            return Error.Validation("notification.push_subscription_invalid");
        }

        var now = clock.GetUtcNow();
        var existing = await db.PushSubscriptions.FirstOrDefaultAsync(s => s.Endpoint == command.Endpoint, ct);
        var result = existing is null
            ? PushSubscription.Create(userId, command.Endpoint, command.Keys.P256dh, command.Keys.Auth, now)
            : existing.Renew(userId, command.Keys.P256dh, command.Keys.Auth, now);
        if (!result.IsSuccess)
        {
            return result.Error!;
        }

        if (existing is null)
        {
            db.PushSubscriptions.Add(result.Value);
        }

        await db.SaveChangesAsync(ct);
        return new PushSubscriptionView(result.Value.Id);
    }

    /// <summary>Sign-out or "turn off on this device": the row goes (idempotent).</summary>
    public async Task UnsubscribeAsync(Guid userId, Guid? id, string? endpoint, CancellationToken ct)
    {
        var query = db.PushSubscriptions.Where(s => s.UserId == userId);
        query = id is { } subscriptionId ? query.Where(s => s.Id == subscriptionId) : query.Where(s => s.Endpoint == endpoint);
        if (id is null && string.IsNullOrWhiteSpace(endpoint))
        {
            return;
        }

        await query.ExecuteDeleteAsync(ct);
    }

    private IQueryable<InboxItem> Visible(Guid userId)
    {
        var now = clock.GetUtcNow();
        return db.InboxItems.AsNoTracking().Where(i => i.UserId == userId && i.VisibleAt <= now);
    }

    private async Task<(int Unread, long Cursor)> CountsAsync(Guid userId, CancellationToken ct)
    {
        var visible = Visible(userId);
        var unread = await visible.CountAsync(i => i.ReadAt == null, ct);
        var cursor = await visible.MaxAsync(i => (long?)i.Seq, ct) ?? 0;
        return (unread, cursor);
    }

    private async Task SignalReadAsync(Guid userId, Guid? itemId, CancellationToken ct)
    {
        try
        {
            await realtime.PublishAsync(userId, new RealtimeSignal(RealtimeSignalTypes.InboxRead, itemId, 0), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Realtime read signal not published");
        }
    }

    private static readonly System.Linq.Expressions.Expression<Func<InboxItem, InboxItemView>> View = i =>
        new InboxItemView(i.Id, i.Kind, i.TitleKey, i.Params, i.Link, i.VisibleAt, i.ReadAt, i.Seq);
}
