using Konnyaku.Academic.Contracts;
using Konnyaku.Application.Messaging;
using Konnyaku.Crm.Contracts;
using Konnyaku.Learning.Contracts;
using Konnyaku.Learning.Domain;
using Konnyaku.Scheduling.Contracts;
using Konnyaku.SharedKernel;
using Konnyaku.Workspace.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Konnyaku.Learning.Application;

public interface ILearningDbContext
{
    DbSet<Attendance> Attendances { get; }
    DbSet<RecordingAccess> RecordingAccesses { get; }

    void Publish(IIntegrationEvent integrationEvent, string? correlationId = null);

    Task<bool> TryAcceptAsync(Guid eventId, string consumer, CancellationToken cancellationToken);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

/// <summary>Turns the meeting participant log into draft attendance for every student holding a seat.</summary>
public sealed class ParticipantsCollectedConsumer(ILearningDbContext db, ISchedulingApi scheduling, IAcademicApi academic, ICrmApi crm)
    : IIntegrationEventHandler<ParticipantsCollectedV1>
{
    public async Task HandleAsync(ParticipantsCollectedV1 @event, IntegrationEventContext context, CancellationToken ct)
    {
        if (!await db.TryAcceptAsync(context.EventId, nameof(ParticipantsCollectedConsumer), ct))
        {
            return;
        }

        var session = await scheduling.GetSessionAsync(@event.SessionId, ct);
        if (session is not null && !await db.Attendances.AnyAsync(a => a.SessionId == session.Id, ct))
        {
            var presence = new Dictionary<Guid, (DateTimeOffset FirstJoin, int Seconds)>();
            foreach (var participant in @event.Participants.Where(p => p.UserId is not null))
            {
                var seconds = Overlap(participant.JoinedAt, participant.LeftAt, session.StartsAt, session.EndsAt);
                foreach (var student in await crm.ListStudentsOfUserAsync(participant.UserId!.Value, ct))
                {
                    presence[student.Id] = presence.TryGetValue(student.Id, out var p)
                        ? (p.FirstJoin < participant.JoinedAt ? p.FirstJoin : participant.JoinedAt, p.Seconds + seconds)
                        : (participant.JoinedAt, seconds);
                }
            }

            foreach (var seat in await academic.GetRosterAsync(session.ClassId, ct))
            {
                var found = presence.TryGetValue(seat.StudentId, out var p);
                var status = seat.Status == "Frozen"
                    ? AttendanceStatus.ExcusedAbsent
                    : AttendancePolicy.Classify(session.StartsAt, session.EndsAt, found ? p.FirstJoin : null, found ? p.Seconds : 0);
                db.Attendances.Add(new Attendance(session.Id, session.ClassId, session.BranchId, session.LocalDate, seat.EnrollmentId,
                    seat.StudentId, status, AttendanceSource.Auto, found ? p.FirstJoin : null, null, found ? p.Seconds : 0));
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private static int Overlap(DateTimeOffset joined, DateTimeOffset left, DateTimeOffset start, DateTimeOffset end)
    {
        var from = joined > start ? joined : start;
        var to = left < end ? left : end;
        return to > from ? (int)(to - from).TotalSeconds : 0;
    }
}

public sealed record AttendanceOverride(Guid EnrollmentId, AttendanceStatus Status);

public sealed record ConfirmAttendanceCommand(IReadOnlyList<AttendanceOverride>? Overrides);

public sealed record AttendanceView(
    Guid Id, Guid EnrollmentId, Guid StudentId, string StudentName, string Status, string Source, int PresentMinutes, bool Confirmed, bool Billable);

/// <summary>The session teacher (or a coordinator) reviews the drafts and confirms; confirmation spends lessons.</summary>
public sealed class AttendanceHandlers(ILearningDbContext db, ISchedulingApi scheduling, IAcademicApi academic, ICrmApi crm, TimeProvider clock)
{
    private static readonly string[] CoordinatorRoles = ["TenantAdmin", "Coordinator"];

    public async Task<Result<IReadOnlyList<AttendanceView>>> ListAsync(Guid sessionId, Guid userId, IReadOnlyCollection<string> roles, CancellationToken ct)
    {
        var session = await AuthorizedSessionAsync(sessionId, userId, roles, ct);
        if (session is null)
        {
            return Error.NotFound("learning.session_not_found");
        }

        return Result<IReadOnlyList<AttendanceView>>.Success(
            await ViewsAsync(await db.Attendances.AsNoTracking().Where(a => a.SessionId == sessionId).ToListAsync(ct), ct));
    }

    public async Task<Result<IReadOnlyList<AttendanceView>>> ConfirmAsync(
        Guid sessionId, Guid userId, IReadOnlyCollection<string> roles, ConfirmAttendanceCommand command, CancellationToken ct)
    {
        var session = await AuthorizedSessionAsync(sessionId, userId, roles, ct);
        if (session is null)
        {
            return Error.NotFound("learning.session_not_found");
        }

        var attendances = await db.Attendances.Where(a => a.SessionId == sessionId).ToListAsync(ct);
        if (attendances.Any(a => a.IsConfirmed))
        {
            return Error.Conflict("learning.already_confirmed");
        }

        if (attendances.Count == 0)
        {
            // No meeting log (offline class, or meeting not recorded): the teacher marks everyone by hand.
            foreach (var seat in await academic.GetRosterAsync(session.ClassId, ct))
            {
                var manual = new Attendance(session.Id, session.ClassId, session.BranchId, session.LocalDate, seat.EnrollmentId, seat.StudentId,
                    seat.Status == "Frozen" ? AttendanceStatus.ExcusedAbsent : AttendanceStatus.Absent, AttendanceSource.Manual, null, null, 0);
                attendances.Add(manual);
                db.Attendances.Add(manual);
            }
        }

        var overrides = (command.Overrides ?? []).ToDictionary(o => o.EnrollmentId, o => o.Status);
        var now = clock.GetUtcNow();
        foreach (var attendance in attendances)
        {
            if (overrides.TryGetValue(attendance.EnrollmentId, out var status))
            {
                attendance.Override(status);
            }

            attendance.Confirm(userId, now);
            db.Publish(new AttendanceConfirmedV1(attendance.Id, attendance.SessionId, attendance.EnrollmentId, attendance.StudentId,
                attendance.BranchId, attendance.LocalDate, attendance.Status.ToString(), attendance.Billable));
        }

        db.Publish(new SessionAttendanceConfirmedV1(sessionId, userId, attendances.Count));
        await db.SaveChangesAsync(ct);
        return Result<IReadOnlyList<AttendanceView>>.Success(await ViewsAsync(attendances, ct));
    }

    private async Task<SessionDto?> AuthorizedSessionAsync(Guid sessionId, Guid userId, IReadOnlyCollection<string> roles, CancellationToken ct)
    {
        var session = await scheduling.GetSessionAsync(sessionId, ct);
        return session is not null && (session.TeacherId == userId || roles.Any(CoordinatorRoles.Contains)) ? session : null;
    }

    private async Task<IReadOnlyList<AttendanceView>> ViewsAsync(IEnumerable<Attendance> attendances, CancellationToken ct)
    {
        var views = new List<AttendanceView>();
        foreach (var a in attendances)
        {
            var student = await crm.GetStudentAsync(a.StudentId, ct);
            views.Add(new AttendanceView(a.Id, a.EnrollmentId, a.StudentId, student?.FullName ?? string.Empty, a.Status.ToString(),
                a.Source.ToString(), a.PresentSeconds / 60, a.IsConfirmed, a.Billable));
        }

        return views.OrderBy(v => v.StudentName).ToList();
    }
}

/// <summary>Applies the recording access policy: by default every enrolled student (absent ones need it most) and the teacher.</summary>
public sealed class RecordingAvailableConsumer(ILearningDbContext db, ISchedulingApi scheduling, IAcademicApi academic, ICrmApi crm)
    : IIntegrationEventHandler<RecordingAvailableV1>
{
    public const RecordingAccessPolicy Policy = RecordingAccessPolicy.AllEnrolled;

    public async Task HandleAsync(RecordingAvailableV1 @event, IntegrationEventContext context, CancellationToken ct)
    {
        if (!await db.TryAcceptAsync(context.EventId, nameof(RecordingAvailableConsumer), ct))
        {
            return;
        }

        var session = await scheduling.GetSessionAsync(@event.SessionId, ct);
        if (session is not null)
        {
            var attended = Policy == RecordingAccessPolicy.AttendeesOnly
                ? (await db.Attendances.Where(a => a.SessionId == session.Id && a.Status != AttendanceStatus.Absent && a.Status != AttendanceStatus.ExcusedAbsent)
                    .Select(a => a.StudentId).ToListAsync(ct)).ToHashSet()
                : null;
            var learners = new List<Guid>();
            foreach (var seat in await academic.GetRosterAsync(session.ClassId, ct))
            {
                if ((attended is null || attended.Contains(seat.StudentId)) && (await crm.GetStudentAsync(seat.StudentId, ct))?.UserId is { } userId)
                {
                    learners.Add(userId);
                    db.RecordingAccesses.Add(new RecordingAccess(session.Id, @event.RecordingId, userId));
                }
            }

            var viewers = session.TeacherId is { } teacher ? learners.Append(teacher).Distinct().ToList() : learners;
            db.Publish(new RecordingAccessGrantedV1(session.Id, @event.RecordingId, viewers));
        }

        await db.SaveChangesAsync(ct);
    }
}

public sealed class RecordingPublishedConsumer(ILearningDbContext db) : IIntegrationEventHandler<RecordingPublishedV1>
{
    public async Task HandleAsync(RecordingPublishedV1 @event, IntegrationEventContext context, CancellationToken ct)
    {
        foreach (var access in await db.RecordingAccesses.Where(r => r.RecordingId == @event.RecordingId).ToListAsync(ct))
        {
            access.MarkPublished();
        }

        await db.SaveChangesAsync(ct);
    }
}

public sealed record RecordingView(Guid SessionId, Guid RecordingId, int SequenceNo, DateTimeOffset StartsAt);

public sealed class RecordingQueries(ILearningDbContext db, ISchedulingApi scheduling)
{
    public async Task<IReadOnlyList<RecordingView>> ListForUserAsync(Guid userId, CancellationToken ct)
    {
        var accesses = await db.RecordingAccesses.AsNoTracking().Where(r => r.UserId == userId && r.Published).ToListAsync(ct);
        var views = new List<RecordingView>();
        foreach (var access in accesses)
        {
            if (await scheduling.GetSessionAsync(access.SessionId, ct) is { } session)
            {
                views.Add(new RecordingView(session.Id, access.RecordingId, session.SequenceNo, session.StartsAt));
            }
        }

        return views.OrderByDescending(v => v.StartsAt).ToList();
    }
}
