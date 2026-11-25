using Konnyaku.Academic.Contracts;
using Konnyaku.Application.Messaging;
using Konnyaku.Billing.Contracts;
using Konnyaku.Catalog.Contracts;
using Konnyaku.Crm.Contracts;
using Konnyaku.Hr.Contracts;
using Konnyaku.Identity.Contracts;
using Konnyaku.Notification.Contracts;
using Konnyaku.Notification.Domain;
using Konnyaku.Scheduling.Contracts;
using Microsoft.Extensions.Logging;

namespace Konnyaku.Notification.Application;

// Every consumer is idempotent through deduplication keys: a redelivered event finds its keys taken and queues nothing.
// Missing data (no email, deleted session) is not an error: the person simply gets no message.

/// <summary>New lead: the assigned consultant, or every tenant admin when nobody was assigned.</summary>
public sealed class LeadCreatedNotifier(
    NotificationRouter router, Recipients recipients, IIdentityApi identity, ICrmContactsApi crm, ICatalogApi catalog, TenantBranding branding)
    : IIntegrationEventHandler<LeadCreatedV1>
{
    public const string AdminRole = "TenantAdmin";

    public async Task HandleAsync(LeadCreatedV1 @event, IntegrationEventContext context, CancellationToken ct)
    {
        var userIds = @event.AssignedConsultantId is { } consultant ? [consultant] : await identity.ListUsersWithRoleAsync(AdminRole, ct);
        var lead = await crm.GetLeadSummaryAsync(@event.LeadId, ct);
        var languages = await catalog.ListLanguagesAsync(ct);
        var timeZone = await branding.TimeZoneAsync(ct);
        foreach (var userId in userIds)
        {
            if (await recipients.UserAsync(userId, ct) is not { } recipient)
            {
                continue;
            }

            var locale = NotificationFormat.NormalizeLocale(recipient.Locale) ?? await branding.DefaultLocaleAsync(ct);
            var languageName = languages.FirstOrDefault(l => l.Code == @event.LanguageCode)?.Name is { } names
                ? names.GetValueOrDefault(locale) ?? names.GetValueOrDefault("vi") ?? @event.LanguageCode
                : @event.LanguageCode;
            var data = new Dictionary<string, string>
            {
                ["studentName"] = lead?.StudentName ?? string.Empty,
                ["languageName"] = languageName,
                ["followUpAt"] = @event.FollowUpDueAt is { } due ? NotificationFormat.DateTime(due, timeZone, locale) : string.Empty,
                ["assigned"] = @event.AssignedConsultantId is null ? string.Empty : "1",
            };
            var parameters = new Dictionary<string, string>
            {
                ["studentName"] = data["studentName"],
                ["languageCode"] = @event.LanguageCode,
                ["languageName"] = languageName,
            };
            await router.RouteAsync(new Notice(InboxKinds.LeadCreated, recipient, $"{NotificationTemplates.LeadCreated}:{@event.LeadId}:{recipient.Key}",
                "/ops/leads", parameters, data), ct);
        }

        await router.SaveAsync(ct);
    }
}

/// <summary>
/// Welcome email for a center-issued student account, sent to the contact email of the linked student profile. The
/// profile is usually linked right after the account is created; if not yet, one more try is scheduled.
/// </summary>
public sealed class LearnerWelcome(
    NotificationQueue queue, Recipients recipients, ICrmContactsApi crm, DispatchSchedule schedule, TimeProvider clock,
    ILogger<LearnerWelcome> logger) : IIntegrationEventHandler<LearnerAccountCreatedV1>, IDispatchJob
{
    public const string StudentRole = "Student";
    public static readonly TimeSpan SecondTry = TimeSpan.FromMinutes(2);

    public string Kind => DispatchKinds.LearnerWelcome;

    public async Task HandleAsync(LearnerAccountCreatedV1 @event, IntegrationEventContext context, CancellationToken ct)
    {
        if (@event.Role != StudentRole)
        {
            return;
        }

        if (!await TrySendAsync(@event.UserId, @event.Username, ct))
        {
            await schedule.ScheduleAsync(DispatchKinds.LearnerWelcome, @event.UserId, clock.GetUtcNow() + SecondTry, ct);
            await queue.SaveAsync(ct);
        }
    }

    public async Task<DateTimeOffset?> RunAsync(Guid refId, CancellationToken cancellationToken)
    {
        if (!await TrySendAsync(refId, null, cancellationToken))
        {
            logger.LogInformation("No reachable student profile for learner {UserId}: no welcome email", refId);
        }

        return null;
    }

    private async Task<bool> TrySendAsync(Guid userId, string? username, CancellationToken ct)
    {
        var contacts = await crm.ListStudentContactsOfUserAsync(userId, ct);
        var sent = false;
        foreach (var contact in contacts)
        {
            if (await recipients.FromContactAsync(contact, ct) is not { Address.Length: > 0 } recipient)
            {
                continue;
            }

            var data = new Dictionary<string, string>
            {
                ["studentName"] = contact.FullName,
                ["username"] = username ?? string.Empty,
            };
            await queue.AddAsync(NotificationTemplates.LearnerWelcome, recipient, data, $"{NotificationTemplates.LearnerWelcome}:{userId}", ct);
            sent = true;
            break;
        }

        if (sent)
        {
            await queue.SaveAsync(ct);
        }

        return sent;
    }
}

/// <summary>Few lessons left. One message per remaining balance, so the same balance is never reported twice.</summary>
public sealed class CreditLowNotifier(NotificationRouter router, Recipients recipients, TenantBranding branding) : IIntegrationEventHandler<CreditLowV1>
{
    public async Task HandleAsync(CreditLowV1 @event, IntegrationEventContext context, CancellationToken ct)
    {
        if (await recipients.StudentAsync(@event.StudentId, ct) is not { } recipient)
        {
            return;
        }

        var locale = NotificationFormat.NormalizeLocale(recipient.Locale) ?? await branding.DefaultLocaleAsync(ct);
        var data = new Dictionary<string, string>
        {
            ["studentName"] = recipient.Name ?? string.Empty,
            ["remaining"] = NotificationFormat.Number(@event.RemainingCredits, locale),
        };
        var remaining = @event.RemainingCredits.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var key = $"{NotificationTemplates.CreditLow}:{@event.OrderId}:{remaining}";
        await router.RouteAsync(new Notice(InboxKinds.CreditLow, recipient, key, "/learn/payments",
            new Dictionary<string, string> { ["remaining"] = remaining }, data), ct);
        await router.SaveAsync(ct);
    }
}

public sealed class EnrollmentActivatedNotifier(NotificationRouter router, Recipients recipients, IAcademicApi academic, TenantBranding branding)
    : IIntegrationEventHandler<EnrollmentActivatedV1>
{
    public async Task HandleAsync(EnrollmentActivatedV1 @event, IntegrationEventContext context, CancellationToken ct)
    {
        if (await recipients.StudentAsync(@event.StudentId, ct) is not { } recipient)
        {
            return;
        }

        var cls = await academic.GetClassAsync(@event.ClassId, ct);
        var locale = NotificationFormat.NormalizeLocale(recipient.Locale) ?? await branding.DefaultLocaleAsync(ct);
        var data = new Dictionary<string, string>
        {
            ["studentName"] = recipient.Name ?? string.Empty,
            ["classCode"] = cls?.Code ?? string.Empty,
            ["startsOn"] = cls is null ? string.Empty : NotificationFormat.Date(cls.StartsOn, locale),
        };
        var parameters = new Dictionary<string, string>
        {
            ["classCode"] = cls?.Code ?? string.Empty,
            ["startsOn"] = cls?.StartsOn.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
        };
        await router.RouteAsync(new Notice(InboxKinds.EnrollmentActivated, recipient, $"{NotificationTemplates.EnrollmentActivated}:{@event.EnrollmentId}",
            "/learn/schedule", parameters, data), ct);
        await router.SaveAsync(ct);
    }
}

/// <summary>The people of a session: active students of its class and its teacher.</summary>
public sealed class SessionAudience(ISchedulingApi scheduling, IAcademicApi academic, Recipients recipients)
{
    public const string ActiveEnrollment = "Active";

    public sealed record Member(Recipient Recipient, string ZonePath);

    public sealed record Audience(SessionDto Session, ClassDto? Class, IReadOnlyList<Member> Members);

    public async Task<Audience?> LoadAsync(Guid sessionId, CancellationToken ct)
    {
        var session = await scheduling.GetSessionAsync(sessionId, ct);
        if (session is null)
        {
            return null;
        }

        var cls = await academic.GetClassAsync(session.ClassId, ct);
        var members = new List<Member>();
        foreach (var entry in (await academic.GetRosterAsync(session.ClassId, ct)).Where(e => e.Status == ActiveEnrollment))
        {
            if (await recipients.StudentAsync(entry.StudentId, ct) is { } student)
            {
                members.Add(new Member(student, "/learn"));
            }
        }

        if (session.TeacherId is { } teacherId && await recipients.UserAsync(teacherId, ct) is { } teacher)
        {
            members.Add(new Member(teacher, "/teach"));
        }

        return new Audience(session, cls, members);
    }

    /// <summary>Inbox parameters, raw: the web app formats the time in the language of the reader and the zone of the session.</summary>
    public static Dictionary<string, string> Params(Audience audience) => new()
    {
        ["classCode"] = audience.Class?.Code ?? string.Empty,
        ["sequenceNo"] = audience.Session.SequenceNo.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["startsAt"] = NotificationFormat.Iso(audience.Session.StartsAt),
        ["timeZone"] = audience.Session.TimeZone,
    };

    /// <summary>Where the session shows for this person: the schedule of the learner, or the home of the teacher.</summary>
    public static string Link(Member member) => member.ZonePath == "/learn" ? "/learn/schedule" : member.ZonePath;

    public static Dictionary<string, string> Data(Audience audience, Member member, string locale) => new()
    {
        ["classCode"] = audience.Class?.Code ?? string.Empty,
        ["sequenceNo"] = audience.Session.SequenceNo.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["startsAt"] = NotificationFormat.DateTime(audience.Session.StartsAt, audience.Session.TimeZone, locale),
        ["timeZone"] = audience.Session.TimeZone,
        ["zonePath"] = member.ZonePath,
    };
}

/// <summary>Plans the 24-hour reminder of every scheduled session; moves it on reschedule, drops it on cancel.</summary>
public sealed class SessionReminderPlanner(DispatchSchedule schedule, INotificationDbContext db, NotificationOptions options, TimeProvider clock)
    : IIntegrationEventHandler<SessionScheduledV1>
{
    public async Task HandleAsync(SessionScheduledV1 @event, IntegrationEventContext context, CancellationToken ct)
    {
        await PlanAsync(@event.SessionId, @event.StartsAt, ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Adds the reminder (not saved) unless the session already started.</summary>
    public async Task PlanAsync(Guid sessionId, DateTimeOffset startsAt, CancellationToken ct)
    {
        if (startsAt <= clock.GetUtcNow())
        {
            await schedule.CancelAsync(DispatchKinds.SessionReminder, sessionId, ct);
            return;
        }

        await schedule.ScheduleAsync(DispatchKinds.SessionReminder, sessionId, startsAt - TimeSpan.FromHours(options.ReminderLeadHours), ct);
    }
}

public sealed class SessionRescheduledNotifier(
    NotificationRouter router, SessionAudience audiences, SessionReminderPlanner reminders, TenantBranding branding)
    : IIntegrationEventHandler<SessionRescheduledV1>
{
    public async Task HandleAsync(SessionRescheduledV1 @event, IntegrationEventContext context, CancellationToken ct)
    {
        await reminders.PlanAsync(@event.SessionId, @event.StartsAt, ct);
        if (await audiences.LoadAsync(@event.SessionId, ct) is { } audience)
        {
            // The event carries the new time; the session read may be newer still (rescheduled again): use the event's.
            audience = audience with { Session = audience.Session with { StartsAt = @event.StartsAt, EndsAt = @event.EndsAt } };
            foreach (var member in audience.Members)
            {
                var locale = NotificationFormat.NormalizeLocale(member.Recipient.Locale) ?? await branding.DefaultLocaleAsync(ct);
                await router.RouteAsync(new Notice(InboxKinds.SessionRescheduled, member.Recipient,
                    $"{NotificationTemplates.SessionRescheduled}:{context.EventId}:{member.Recipient.Key}", SessionAudience.Link(member),
                    SessionAudience.Params(audience), SessionAudience.Data(audience, member, locale)), ct);
            }
        }

        await router.SaveAsync(ct);
    }
}

public sealed class SessionCancelledNotifier(NotificationRouter router, SessionAudience audiences, DispatchSchedule schedule, TenantBranding branding)
    : IIntegrationEventHandler<SessionCancelledV1>
{
    public async Task HandleAsync(SessionCancelledV1 @event, IntegrationEventContext context, CancellationToken ct)
    {
        await schedule.CancelAsync(DispatchKinds.SessionReminder, @event.SessionId, ct);
        if (await audiences.LoadAsync(@event.SessionId, ct) is { } audience)
        {
            foreach (var member in audience.Members)
            {
                var locale = NotificationFormat.NormalizeLocale(member.Recipient.Locale) ?? await branding.DefaultLocaleAsync(ct);
                var data = SessionAudience.Data(audience, member, locale);
                data["reason"] = @event.Reason ?? string.Empty;
                var parameters = SessionAudience.Params(audience);
                parameters["reason"] = @event.Reason ?? string.Empty;
                await router.RouteAsync(new Notice(InboxKinds.SessionCancelled, member.Recipient,
                    $"{NotificationTemplates.SessionCancelled}:{@event.SessionId}:{member.Recipient.Key}", SessionAudience.Link(member), parameters, data), ct);
            }
        }

        await router.SaveAsync(ct);
    }
}

/// <summary>Runs 24 hours before a session: reminds its students and teacher (once per start time).</summary>
public sealed class SessionReminderJob(
    NotificationRouter router, SessionAudience audiences, TenantBranding branding, NotificationOptions options, TimeProvider clock)
    : IDispatchJob
{
    private static readonly string[] Upcoming = ["Scheduled", "Provisioned"];

    public string Kind => DispatchKinds.SessionReminder;

    public async Task<DateTimeOffset?> RunAsync(Guid refId, CancellationToken cancellationToken)
    {
        var audience = await audiences.LoadAsync(refId, cancellationToken);
        var now = clock.GetUtcNow();
        if (audience is null || !Upcoming.Contains(audience.Session.Status) || audience.Session.StartsAt <= now)
        {
            return null;
        }

        var lead = TimeSpan.FromHours(options.ReminderLeadHours);
        if (audience.Session.StartsAt - now > lead + TimeSpan.FromHours(1))
        {
            // Moved later without us hearing of it yet: come back at the right time.
            return audience.Session.StartsAt - lead;
        }

        var stamp = audience.Session.StartsAt.UtcDateTime.ToString("yyyyMMddHHmm", System.Globalization.CultureInfo.InvariantCulture);
        foreach (var member in audience.Members)
        {
            var locale = NotificationFormat.NormalizeLocale(member.Recipient.Locale) ?? await branding.DefaultLocaleAsync(cancellationToken);
            await router.RouteAsync(new Notice(InboxKinds.SessionReminder, member.Recipient,
                $"{NotificationTemplates.SessionReminder}:{refId}:{stamp}:{member.Recipient.Key}", SessionAudience.Link(member),
                SessionAudience.Params(audience), SessionAudience.Data(audience, member, locale)), cancellationToken);
        }

        await router.SaveAsync(cancellationToken);
        return null;
    }
}

/// <summary>Payslips are final: tell the teachers (all teachers of the tenant; Hr exposes no per-period list).</summary>
public sealed class PayrollLockedNotifier(NotificationRouter router, Recipients recipients, IIdentityApi identity, TenantBranding branding)
    : IIntegrationEventHandler<PayrollPeriodLockedV1>
{
    public const string TeacherRole = "Teacher";

    public async Task HandleAsync(PayrollPeriodLockedV1 @event, IntegrationEventContext context, CancellationToken ct)
    {
        foreach (var userId in await identity.ListUsersWithRoleAsync(TeacherRole, ct))
        {
            if (await recipients.UserAsync(userId, ct) is not { } recipient)
            {
                continue;
            }

            var locale = NotificationFormat.NormalizeLocale(recipient.Locale) ?? await branding.DefaultLocaleAsync(ct);
            var data = new Dictionary<string, string>
            {
                ["from"] = NotificationFormat.ShortDate(@event.From, locale),
                ["to"] = NotificationFormat.ShortDate(@event.To, locale),
            };
            var parameters = new Dictionary<string, string>
            {
                ["from"] = @event.From.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                ["to"] = @event.To.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            };
            await router.RouteAsync(new Notice(InboxKinds.PayrollLocked, recipient, $"{NotificationTemplates.PayrollLocked}:{@event.PeriodId}:{recipient.Key}",
                "/teach", parameters, data), ct);
        }

        await router.SaveAsync(ct);
    }
}

/// <summary>Another module asked for a notification through its outbox.</summary>
public sealed class NotificationRequestedConsumer(INotificationApi notifications, ILogger<NotificationRequestedConsumer> logger)
    : IIntegrationEventHandler<NotificationRequestedV1>
{
    public async Task HandleAsync(NotificationRequestedV1 @event, IntegrationEventContext context, CancellationToken ct)
    {
        var result = await notifications.EnqueueAsync(new NotificationRequest(@event.TemplateKey,
            new NotificationRecipient(@event.RecipientUserId, @event.RecipientAddress, @event.RecipientName, @event.Locale),
            @event.Data, @event.DeduplicationKey, @event.Channel, @event.Sensitive), ct);
        if (!result.IsSuccess)
        {
            logger.LogWarning("Notification {Template} not queued: {Code}", @event.TemplateKey, result.Error!.Code);
        }
    }
}
