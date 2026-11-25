using System.Globalization;
using Konnyaku.Academic.Contracts;
using Konnyaku.Application.Authorization;
using Konnyaku.Application.Messaging;
using Konnyaku.Application.Persistence;
using Konnyaku.Organization.Contracts;
using Konnyaku.Scheduling.Contracts;
using Konnyaku.Scheduling.Domain;
using Konnyaku.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Konnyaku.Scheduling.Application;

/// <param name="EffectiveFrom">First local date of the new rule (default: today in the class time zone; never in the past).</param>
public sealed record ReplanScheduleCommand(int[] DaysOfWeek, TimeOnly StartTime, DateOnly? EffectiveFrom, Guid? TeacherId, Guid? RoomId);

public sealed record ReplanResult(int Replanned, IReadOnlyList<SessionDto> Sessions);

/// <summary>
/// Changes the weekly schedule of a scheduled class. Sessions that already took place (or started), cancelled ones and
/// those before <see cref="ReplanScheduleCommand.EffectiveFrom"/> stay as they are; every later planned session keeps its
/// lesson and order but moves to the new days, time, teacher and room, after the last session that stays. Bookings are
/// re-made under the exclusion constraints (all or nothing), and Workspace is told so it returns and re-acquires hosts.
/// </summary>
public sealed class ReplanScheduleHandler(
    ISchedulingDbContext db, IAcademicApi academic, IOrganizationApi organization, ResourceValidator validator,
    IDbErrorClassifier errors, TimeProvider clock, IAccessControl access)
{
    public async Task<Result<ReplanResult>> HandleAsync(Guid classId, ReplanScheduleCommand command, CancellationToken ct)
    {
        var cls = await academic.GetClassAsync(classId, ct);
        if (cls is null)
        {
            return Error.NotFound("scheduling.class_not_found");
        }

        if (await access.EnsureAsync(Permissions.Schedules, ct, cls.BranchId) is { } forbidden)
        {
            return forbidden;
        }

        if (cls.Status is "Completed" or "Cancelled")
        {
            return Error.Conflict("scheduling.class_closed");
        }

        if (command.DaysOfWeek is not { Length: > 0 } || command.DaysOfWeek.Any(d => d is < 1 or > 7))
        {
            return Error.Validation("scheduling.days_invalid");
        }

        if (await validator.ValidateAsync(command.TeacherId, command.RoomId, cls.BranchId, ct) is { } invalid)
        {
            return invalid;
        }

        var now = clock.GetUtcNow();
        var today = SchedulePlanner.Today(now, cls.TimeZone);
        var from = command.EffectiveFrom ?? today;
        if (from < today)
        {
            return Error.Validation("scheduling.effective_from_past");
        }

        var sessions = await db.Sessions.Where(s => s.ClassId == classId).OrderBy(s => s.SequenceNo).ToListAsync(ct);
        if (sessions.All(s => s.Status == SessionStatus.Cancelled))
        {
            return Error.Conflict("scheduling.not_scheduled");
        }

        var replanned = sessions.Where(s => s.IsChangeable && s.StartsAt > now && s.LocalDate >= from).ToList();
        if (replanned.Count == 0)
        {
            return Error.Conflict("scheduling.nothing_to_replan");
        }

        // New slots start after the last session that stays (lessons keep their order), never before the class starts.
        var kept = sessions.Where(s => s.Status != SessionStatus.Cancelled && !replanned.Contains(s)).ToList();
        var start = new[] { from, cls.StartsOn, kept.Count == 0 ? from : kept.Max(s => s.LocalDate).AddDays(1) }.Max();
        var daysOff = await organization.ListHolidaysAsync(cls.BranchId, start, start.AddDays(SchedulePlanner.MaxDaysAhead), ct);
        var slots = SchedulePlanner.Plan(start, command.DaysOfWeek, command.StartTime, cls.SessionMinutes, cls.TimeZone, replanned.Count + 1, daysOff)
            .Where(slot => slot.StartsAt > now).Take(replanned.Count).ToList();
        if (slots.Count < replanned.Count)
        {
            return Error.Validation("scheduling.plan_too_long");
        }

        var pattern = await db.Patterns.Where(p => p.ClassId == classId).OrderByDescending(p => p.CreatedAt).FirstOrDefaultAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // Phase 1: free the resources of every moving session, so sessions of the same class can swap slots.
            var ids = replanned.Select(s => s.Id).ToList();
            foreach (var booking in await db.Bookings.Where(b => ids.Contains(b.SessionId) && b.IsActive).ToListAsync(ct))
            {
                booking.Release();
            }

            await db.SaveChangesAsync(ct);

            // Phase 2: new slots and bookings; PostgreSQL rejects any overlap with another class.
            for (var i = 0; i < replanned.Count; i++)
            {
                var session = replanned[i];
                var moved = session.StartsAt != slots[i].StartsAt || session.EndsAt != slots[i].EndsAt;
                var result = session.Replan(slots[i], command.TeacherId, command.RoomId);
                if (!result.IsSuccess)
                {
                    return result.Error!;
                }

                Book(session, command.TeacherId, command.RoomId);
                if (moved)
                {
                    db.Publish(new SessionRescheduledV1(session.Id, session.StartsAt, session.EndsAt));
                }
            }

            if (pattern is null)
            {
                db.Patterns.Add(new SchedulePattern(classId, command.DaysOfWeek, command.StartTime, cls.SessionMinutes, cls.TimeZone, start,
                    command.TeacherId, command.RoomId, now));
            }
            else
            {
                pattern.Replan(command.DaysOfWeek, command.StartTime, cls.SessionMinutes, start, command.TeacherId, command.RoomId);
            }

            db.LogChange(nameof(SchedulePattern), pattern?.Id ?? classId, classId, "Updated",
                [("sessionsReplanned", null, replanned.Count.ToString(CultureInfo.InvariantCulture))]);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (errors.IsExclusionViolation(ex))
        {
            return Error.Conflict("scheduling.resource_conflict");
        }

        return new ReplanResult(replanned.Count, sessions.Select(s => s.ToDto()).ToList());
    }

    private void Book(Session session, Guid? teacherId, Guid? roomId)
    {
        if (teacherId is { } t)
        {
            db.Bookings.Add(new ResourceBooking(session.Id, ResourceTypes.Teacher, t, session.StartsAt, session.EndsAt));
        }

        if (roomId is { } r)
        {
            db.Bookings.Add(new ResourceBooking(session.Id, ResourceTypes.Room, r, session.StartsAt, session.EndsAt));
        }
    }
}

/// <summary>
/// The class changed delivery mode: its future planned sessions follow, and Workspace re-evaluates their meeting rooms
/// (an offline session returns its host; an online one gets one). Idempotent: only sessions still on another mode change.
/// </summary>
public sealed class ClassDeliveryModeChangedConsumer(ISchedulingDbContext db, TimeProvider clock) : IIntegrationEventHandler<ClassDeliveryModeChangedV1>
{
    public async Task HandleAsync(ClassDeliveryModeChangedV1 @event, IntegrationEventContext context, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var sessions = await db.Sessions
            .Where(s => s.ClassId == @event.ClassId && s.StartsAt > now && s.DeliveryMode != @event.DeliveryMode &&
                        (s.Status == SessionStatus.Scheduled || s.Status == SessionStatus.Provisioned))
            .ToListAsync(ct);
        foreach (var session in sessions)
        {
            session.ChangeDeliveryMode(@event.DeliveryMode);
            db.Publish(new SessionRescheduledV1(session.Id, session.StartsAt, session.EndsAt));
        }

        await db.SaveChangesAsync(ct);
    }
}

/// <summary>A class was completed or cancelled: its remaining planned sessions are cancelled and their resources freed.</summary>
public sealed class ClassClosedConsumer(ISchedulingDbContext db, TimeProvider clock) : IIntegrationEventHandler<ClassClosedV1>
{
    public async Task HandleAsync(ClassClosedV1 @event, IntegrationEventContext context, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var sessions = await db.Sessions
            .Where(s => s.ClassId == @event.ClassId && s.StartsAt > now &&
                        (s.Status == SessionStatus.Scheduled || s.Status == SessionStatus.Provisioned))
            .ToListAsync(ct);
        var ids = sessions.Select(s => s.Id).ToList();
        foreach (var booking in await db.Bookings.Where(b => ids.Contains(b.SessionId) && b.IsActive).ToListAsync(ct))
        {
            booking.Release();
        }

        var reason = $"class_{@event.Status.ToLowerInvariant()}";
        foreach (var session in sessions)
        {
            session.Cancel(reason);
            db.Publish(new SessionCancelledV1(session.Id, reason));
        }

        await db.SaveChangesAsync(ct);
    }
}
