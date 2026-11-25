using System.Globalization;
using Konnyaku.Academic.Contracts;
using Konnyaku.Application.Messaging;
using Konnyaku.Assessment.Contracts;
using Konnyaku.Notification.Domain;
using Konnyaku.Scheduling.Contracts;

namespace Konnyaku.Notification.Application;

// Bookings (waitlist, make-ups) and assessments: in-app and Web Push only (no email templates for these kinds yet).
// Idempotent through the deduplication key of the inbox item.

/// <summary>A freed seat is held for a waitlisted learner: tell them before the hold runs out.</summary>
public sealed class WaitlistSeatOfferedNotifier(NotificationRouter router, Recipients recipients, ISchedulingApi scheduling, IAcademicApi academic)
    : IIntegrationEventHandler<WaitlistSeatOfferedV1>
{
    public async Task HandleAsync(WaitlistSeatOfferedV1 @event, IntegrationEventContext context, CancellationToken ct)
    {
        if (await recipients.StudentAsync(@event.StudentId, ct) is not { } recipient)
        {
            return;
        }

        var parameters = await SessionParams.LoadAsync(scheduling, academic, @event.SessionId, ct);
        parameters["expiresAt"] = NotificationFormat.Iso(@event.ExpiresAt);
        await router.RouteAsync(new Notice(InboxKinds.WaitlistOffered, recipient, $"{InboxKinds.WaitlistOffered}:{@event.EntryId}", "/learn/schedule",
            parameters), ct);
        await router.SaveAsync(ct);
    }
}

public sealed class MakeupApprovedNotifier(NotificationRouter router, Recipients recipients, ISchedulingApi scheduling, IAcademicApi academic)
    : IIntegrationEventHandler<MakeupApprovedV1>
{
    public async Task HandleAsync(MakeupApprovedV1 @event, IntegrationEventContext context, CancellationToken ct)
    {
        if (await recipients.StudentAsync(@event.StudentId, ct) is not { } recipient)
        {
            return;
        }

        var parameters = await SessionParams.LoadAsync(scheduling, academic, @event.SessionId, ct);
        await router.RouteAsync(new Notice(InboxKinds.MakeupApproved, recipient, $"{InboxKinds.MakeupApproved}:{@event.BookingId}", "/learn/schedule",
            parameters), ct);
        await router.SaveAsync(ct);
    }
}

internal static class SessionParams
{
    public static async Task<Dictionary<string, string>> LoadAsync(ISchedulingApi scheduling, IAcademicApi academic, Guid sessionId, CancellationToken ct)
    {
        var session = await scheduling.GetSessionAsync(sessionId, ct);
        var cls = session is null ? null : await academic.GetClassAsync(session.ClassId, ct);
        return new Dictionary<string, string>
        {
            ["classCode"] = cls?.Code ?? string.Empty,
            ["sequenceNo"] = session?.SequenceNo.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            ["startsAt"] = session is null ? string.Empty : NotificationFormat.Iso(session.StartsAt),
            ["timeZone"] = session?.TimeZone ?? string.Empty,
        };
    }
}

/// <summary>
/// Plans the notices of an assignment: "new assignment" when it opens, and a reminder before its deadline for whoever
/// has not handed it in. Both move when the dates change; a deleted assignment simply finds nobody to tell.
/// </summary>
public sealed class AssignmentPlanner(DispatchSchedule schedule, INotificationDbContext db, NotificationOptions options, TimeProvider clock)
    : IIntegrationEventHandler<AssignmentPublishedV1>, IIntegrationEventHandler<AssignmentRescheduledV1>
{
    public Task HandleAsync(AssignmentPublishedV1 @event, IntegrationEventContext context, CancellationToken ct) =>
        PlanAsync(@event.AssignmentId, @event.OpensAt, @event.DueAt, ct);

    public Task HandleAsync(AssignmentRescheduledV1 @event, IntegrationEventContext context, CancellationToken ct) =>
        PlanAsync(@event.AssignmentId, @event.OpensAt, @event.DueAt, ct);

    private async Task PlanAsync(Guid assignmentId, DateTimeOffset opensAt, DateTimeOffset? dueAt, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var openAt = opensAt > now ? opensAt : now;
        await schedule.ScheduleAsync(DispatchKinds.AssignmentOpen, assignmentId, openAt, ct);

        var remindAt = dueAt - TimeSpan.FromHours(options.AssignmentDueLeadHours);
        if (dueAt is { } due && due > now && remindAt is { } at && at > opensAt)
        {
            await schedule.ScheduleAsync(DispatchKinds.AssignmentDue, assignmentId, at > now ? at : now, ct);
        }
        else
        {
            await schedule.CancelAsync(DispatchKinds.AssignmentDue, assignmentId, ct);
        }

        await db.SaveChangesAsync(ct);
    }
}

/// <summary>At its opening time: every learner the assignment is for hears of it (once).</summary>
public sealed class AssignmentOpenJob(NotificationRouter router, Recipients recipients, IAssessmentApi assessment, TimeProvider clock) : IDispatchJob
{
    public string Kind => DispatchKinds.AssignmentOpen;

    public async Task<DateTimeOffset?> RunAsync(Guid refId, CancellationToken cancellationToken)
    {
        var audience = await assessment.GetAssignmentAudienceAsync(refId, cancellationToken);
        if (audience is null)
        {
            return null;
        }

        if (audience.OpensAt > clock.GetUtcNow() + TimeSpan.FromMinutes(1))
        {
            // Moved later without us hearing of it yet: come back at the right time.
            return audience.OpensAt;
        }

        var parameters = AssignmentParams(audience);
        foreach (var studentId in audience.StudentIds)
        {
            if (await recipients.StudentAsync(studentId, cancellationToken) is { } recipient)
            {
                await router.RouteAsync(new Notice(InboxKinds.AssessmentAssigned, recipient, $"{InboxKinds.AssessmentAssigned}:{refId}:{recipient.Key}",
                    $"/learn/assessments/{refId}", parameters), cancellationToken);
            }
        }

        await router.SaveAsync(cancellationToken);
        return null;
    }

    public static Dictionary<string, string> AssignmentParams(AssignmentAudienceDto audience) => new()
    {
        ["title"] = audience.Title,
        ["assessmentKind"] = audience.Kind,
        ["dueAt"] = audience.DueAt is { } due ? NotificationFormat.Iso(due) : string.Empty,
    };
}

/// <summary>Before the deadline: learners who have not handed the assignment in get a reminder (once per deadline).</summary>
public sealed class AssignmentDueJob(
    NotificationRouter router, Recipients recipients, IAssessmentApi assessment, NotificationOptions options, TimeProvider clock) : IDispatchJob
{
    public string Kind => DispatchKinds.AssignmentDue;

    public async Task<DateTimeOffset?> RunAsync(Guid refId, CancellationToken cancellationToken)
    {
        var audience = await assessment.GetAssignmentAudienceAsync(refId, cancellationToken);
        var now = clock.GetUtcNow();
        if (audience?.DueAt is not { } due || due <= now)
        {
            return null;
        }

        var lead = TimeSpan.FromHours(options.AssignmentDueLeadHours);
        if (due - now > lead + TimeSpan.FromHours(1))
        {
            return due - lead;
        }

        var stamp = due.UtcDateTime.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);
        var parameters = AssignmentOpenJob.AssignmentParams(audience);
        foreach (var studentId in audience.StudentIds.Except(audience.SubmittedStudentIds))
        {
            if (await recipients.StudentAsync(studentId, cancellationToken) is { } recipient)
            {
                await router.RouteAsync(new Notice(InboxKinds.AssessmentDueSoon, recipient,
                    $"{InboxKinds.AssessmentDueSoon}:{refId}:{stamp}:{recipient.Key}", $"/learn/assessments/{refId}", parameters), cancellationToken);
            }
        }

        await router.SaveAsync(cancellationToken);
        return null;
    }
}

/// <summary>A score is official: the learner hears of it when it becomes visible to them (release policy of the assignment).</summary>
public sealed class AssessmentGradedNotifier(NotificationRouter router, Recipients recipients, IAssessmentApi assessment)
    : IIntegrationEventHandler<AssessmentGradedV1>
{
    public async Task HandleAsync(AssessmentGradedV1 @event, IntegrationEventContext context, CancellationToken ct)
    {
        if (await recipients.StudentAsync(@event.StudentId, ct) is not { } recipient)
        {
            return;
        }

        var audience = await assessment.GetAssignmentAudienceAsync(@event.AssignmentId, ct);
        var score = @event.Score.ToString("0.##", CultureInfo.InvariantCulture);
        var parameters = new Dictionary<string, string>
        {
            ["title"] = audience?.Title ?? string.Empty,
            ["assessmentKind"] = @event.Kind,
            ["score"] = score,
            ["maxScore"] = @event.MaxScore.ToString("0.##", CultureInfo.InvariantCulture),
        };
        // A regrade re-sends the event with the new score: a new notice for a new score, never twice the same one.
        await router.RouteAsync(new Notice(InboxKinds.AssessmentGraded, recipient, $"{InboxKinds.AssessmentGraded}:{@event.AttemptId}:{score}",
            $"/learn/attempts/{@event.AttemptId}/result", parameters, VisibleAt: @event.VisibleFrom), ct);
        await router.SaveAsync(ct);
    }
}
