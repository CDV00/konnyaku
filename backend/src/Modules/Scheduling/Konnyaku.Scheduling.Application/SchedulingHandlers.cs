using System.Globalization;
using Konnyaku.Academic.Contracts;
using Konnyaku.Application.Authorization;
using Konnyaku.Application.Persistence;
using Konnyaku.Crm.Contracts;
using Konnyaku.Identity.Contracts;
using Konnyaku.Organization.Contracts;
using Konnyaku.Scheduling.Contracts;
using Konnyaku.Scheduling.Domain;
using Konnyaku.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Konnyaku.Scheduling.Application;

public interface ISchedulingDbContext
{
    DbSet<SchedulePattern> Patterns { get; }
    DbSet<Session> Sessions { get; }
    DbSet<ResourceBooking> Bookings { get; }
    DbSet<SessionSeat> Seats { get; }
    DbSet<SessionBooking> SessionBookings { get; }
    DbSet<MakeupEntitlement> Entitlements { get; }
    DbSet<SessionWaitlistEntry> Waitlist { get; }
    DbSet<BookingSettings> Settings { get; }
    DatabaseFacade Database { get; }

    /// <summary>Adds the seat row of a session unless one exists (INSERT ... ON CONFLICT DO NOTHING, outside tracked changes).</summary>
    Task InsertSeatIfMissingAsync(SessionSeat seat, CancellationToken cancellationToken);

    void Publish(IIntegrationEvent integrationEvent, string? correlationId = null);

    void LogChange(string entityType, Guid entityId, Guid subjectId, string action, IReadOnlyList<(string Field, string? Old, string? New)> changes);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

internal static class SchedulingErrors
{
    public static readonly Error ResourceConflict = Error.Conflict("scheduling.resource_conflict");
    public static readonly Error SessionNotFound = Error.NotFound("scheduling.session_not_found");
    public static readonly Error TeacherInvalid = Error.Validation("scheduling.teacher_invalid");
}

/// <summary>Validates that a user is a teacher of the current tenant and a room belongs to the class branch.</summary>
public sealed class ResourceValidator(IIdentityApi identity, IOrganizationApi organization)
{
    public const string TeacherRole = "Teacher";

    public async Task<Error?> ValidateAsync(Guid? teacherId, Guid? roomId, Guid branchId, CancellationToken ct)
    {
        if (teacherId is { } t && (await identity.GetMemberAsync(t, ct))?.Roles.Contains(TeacherRole) != true)
        {
            return SchedulingErrors.TeacherInvalid;
        }

        if (roomId is { } r && await organization.GetRoomAsync(r, ct) is var room && (room is null || room.BranchId != branchId || !room.IsActive))
        {
            return Error.Validation("scheduling.room_invalid");
        }

        return null;
    }
}

public sealed record ScheduleClassCommand(int[] DaysOfWeek, TimeOnly StartTime, DateOnly? EffectiveFrom, Guid? TeacherId, Guid? RoomId);

/// <summary>Materializes every lesson of a class into sessions, skipping days off, and books teacher and room.</summary>
public sealed class ScheduleClassHandler(
    ISchedulingDbContext db, IAcademicApi academic, IOrganizationApi organization, ResourceValidator validator,
    IDbErrorClassifier errors, TimeProvider clock, IAccessControl access, BookingSync bookings)
{
    public async Task<Result<IReadOnlyList<SessionDto>>> HandleAsync(Guid classId, ScheduleClassCommand command, CancellationToken ct)
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

        if (command.DaysOfWeek is not { Length: > 0 } || command.DaysOfWeek.Any(d => d is < 1 or > 7))
        {
            return Error.Validation("scheduling.days_invalid");
        }

        if (await db.Sessions.AnyAsync(s => s.ClassId == classId && s.Status != SessionStatus.Cancelled, ct))
        {
            return Error.Conflict("scheduling.already_scheduled");
        }

        if (await validator.ValidateAsync(command.TeacherId, command.RoomId, cls.BranchId, ct) is { } invalid)
        {
            return invalid;
        }

        var lessons = await academic.GetLessonsAsync(classId, ct);
        var from = command.EffectiveFrom ?? cls.StartsOn;
        var daysOff = await organization.ListHolidaysAsync(cls.BranchId, from, from.AddDays(SchedulePlanner.MaxDaysAhead), ct);
        var slots = SchedulePlanner.Plan(from, command.DaysOfWeek, command.StartTime, cls.SessionMinutes, cls.TimeZone, lessons.Count, daysOff);

        var pattern = new SchedulePattern(classId, command.DaysOfWeek, command.StartTime, cls.SessionMinutes, cls.TimeZone,
            from, command.TeacherId, command.RoomId, clock.GetUtcNow());
        db.Patterns.Add(pattern);

        var sessions = new List<Session>();
        for (var i = 0; i < slots.Count; i++)
        {
            var session = new Session(classId, cls.BranchId, pattern.Id, lessons[i].Id, i + 1, slots[i], cls.TimeZone,
                cls.DeliveryMode, command.TeacherId, command.RoomId);
            sessions.Add(session);
            db.Sessions.Add(session);
            Book(session, command.TeacherId, command.RoomId);
            db.Publish(new SessionScheduledV1(session.Id, classId, cls.BranchId, session.StartsAt, session.EndsAt,
                command.TeacherId, cls.DeliveryMode));
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (errors.IsExclusionViolation(ex))
        {
            return SchedulingErrors.ResourceConflict;
        }

        // Learners enrolled before the schedule existed get their seats now.
        await bookings.SyncClassAsync(classId, ct);
        return sessions.Select(s => s.ToDto()).ToList();
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

public sealed record RescheduleCommand(DateOnly Date, TimeOnly StartTime);

public sealed record AssignTeacherCommand(Guid TeacherId);

public sealed record CancelSessionCommand(string Reason);

/// <summary>
/// Changes one session (move, teacher, cancel) — branch managers and coordinators for sessions of their branches. Each
/// change is written to the class's change log.
/// </summary>
public sealed class SessionChangeHandler(
    ISchedulingDbContext db, ResourceValidator validator, IDbErrorClassifier errors, IAccessControl access, BookingSync bookings)
{
    public async Task<Result<SessionDto>> RescheduleAsync(Guid sessionId, RescheduleCommand command, CancellationToken ct)
    {
        var (session, denied) = await LoadAsync(sessionId, ct);
        if (session is null)
        {
            return denied!;
        }

        var before = session.StartsAt;

        var duration = (int)(session.EndsAt - session.StartsAt).TotalMinutes;
        var moved = session.MoveTo(SchedulePlanner.Slot(command.Date, command.StartTime, duration, session.TimeZone));
        if (!moved.IsSuccess)
        {
            return moved.Error!;
        }

        foreach (var booking in await ActiveBookingsAsync(sessionId, ct))
        {
            booking.MoveTo(session.StartsAt, session.EndsAt);
        }

        db.Publish(new SessionRescheduledV1(session.Id, session.StartsAt, session.EndsAt));
        Log(session, ("startsAt", Text(before), Text(session.StartsAt)));
        return await SaveAsync(session, ct);
    }

    public async Task<Result<SessionDto>> AssignTeacherAsync(Guid sessionId, AssignTeacherCommand command, CancellationToken ct)
    {
        var (session, denied) = await LoadAsync(sessionId, ct);
        if (session is null)
        {
            return denied!;
        }

        var before = session.ActualTeacherId;

        if (await validator.ValidateAsync(command.TeacherId, null, session.BranchId, ct) is { } invalid)
        {
            return invalid;
        }

        var assigned = session.AssignTeacher(command.TeacherId);
        if (!assigned.IsSuccess)
        {
            return assigned.Error!;
        }

        foreach (var booking in (await ActiveBookingsAsync(sessionId, ct)).Where(b => b.ResourceType == ResourceTypes.Teacher))
        {
            booking.Release();
        }

        db.Bookings.Add(new ResourceBooking(session.Id, ResourceTypes.Teacher, command.TeacherId, session.StartsAt, session.EndsAt));
        Log(session, ("teacherId", before?.ToString(), command.TeacherId.ToString()));
        return await SaveAsync(session, ct);
    }

    public async Task<Result<SessionDto>> CancelAsync(Guid sessionId, CancelSessionCommand command, CancellationToken ct)
    {
        var (session, denied) = await LoadAsync(sessionId, ct);
        if (session is null)
        {
            return denied!;
        }

        var cancelled = session.Cancel(string.IsNullOrWhiteSpace(command.Reason) ? "unspecified" : command.Reason.Trim());
        if (!cancelled.IsSuccess)
        {
            return cancelled.Error!;
        }

        foreach (var booking in await ActiveBookingsAsync(sessionId, ct))
        {
            booking.Release();
        }

        await bookings.ReleaseSessionsAsync([session.Id], ct);
        db.Publish(new SessionCancelledV1(session.Id, session.CancelReason!));
        Log(session, ("status", nameof(SessionStatus.Scheduled), nameof(SessionStatus.Cancelled)), ("cancelReason", null, session.CancelReason));
        return await SaveAsync(session, ct);
    }

    private async Task<(Session? Session, Error? Denied)> LoadAsync(Guid sessionId, CancellationToken ct)
    {
        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null)
        {
            return (null, SchedulingErrors.SessionNotFound);
        }

        return await access.EnsureAsync(Permissions.Schedules, ct, session.BranchId) is { } forbidden ? (null, forbidden) : (session, null);
    }

    private void Log(Session session, params (string Field, string? Old, string? New)[] changes) =>
        db.LogChange(nameof(Session), session.Id, session.ClassId, "Updated",
            [("sequenceNo", null, session.SequenceNo.ToString(CultureInfo.InvariantCulture)), .. changes]);

    internal static string Text(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private Task<List<ResourceBooking>> ActiveBookingsAsync(Guid sessionId, CancellationToken ct) =>
        db.Bookings.Where(b => b.SessionId == sessionId && b.IsActive).ToListAsync(ct);

    private async Task<Result<SessionDto>> SaveAsync(Session session, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return session.ToDto();
        }
        catch (DbUpdateException ex) when (errors.IsExclusionViolation(ex))
        {
            return SchedulingErrors.ResourceConflict;
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("scheduling.session_changed_concurrently");
        }
    }
}

public sealed class SchedulingQueries(ISchedulingDbContext db, ICrmApi crm, IAcademicApi academic, BookingSync sync) : ISchedulingApi
{
    public async Task<IReadOnlyList<SessionRosterEntryDto>> GetSessionRosterAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await db.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
        if (session is null)
        {
            return [];
        }

        // A session scheduled before bookings existed has no seat row: its Regular bookings are made on first read.
        if (!await db.Seats.AnyAsync(s => s.SessionId == sessionId, cancellationToken))
        {
            await sync.SyncClassAsync(session.ClassId, cancellationToken, sessionId);
        }

        return await db.SessionBookings.AsNoTracking()
            .Where(b => b.SessionId == sessionId &&
                        (b.Status == BookingStatus.Booked || b.Status == BookingStatus.Attended || b.Status == BookingStatus.NoShow))
            .OrderBy(b => b.CreatedAt)
            .Select(b => new SessionRosterEntryDto(b.Id, b.EnrollmentId, b.StudentId, b.Kind.ToString(), b.Mode.ToString(), b.Status.ToString()))
            .ToListAsync(cancellationToken);
    }

    public async Task<SessionDto?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken) =>
        (await db.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken))?.ToDto();

    /// <summary>The weekly rule currently used by a class (for the edit form).</summary>
    public async Task<PatternView?> GetPatternAsync(Guid classId, CancellationToken ct) =>
        await db.Patterns.AsNoTracking().Where(p => p.ClassId == classId).OrderByDescending(p => p.CreatedAt)
            .Select(p => new PatternView(p.Id, p.ClassId, p.IsoDays, p.StartTime, p.DurationMinutes, p.TimeZone, p.EffectiveFrom, p.TeacherId, p.RoomId))
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<Guid>> ListClassIdsOfTeacherAsync(Guid teacherId, CancellationToken cancellationToken) =>
        await db.Sessions.AsNoTracking()
            .Where(s => s.PlannedTeacherId == teacherId || s.ActualTeacherId == teacherId)
            .Select(s => s.ClassId).Distinct().ToListAsync(cancellationToken);

    public Task<int> CountClassSessionsAsync(Guid classId, CancellationToken cancellationToken) =>
        db.Sessions.CountAsync(s => s.ClassId == classId && s.Status != SessionStatus.Cancelled, cancellationToken);

    public async Task<IReadOnlyList<SessionDto>> ListClassSessionsAsync(Guid classId, CancellationToken ct) =>
        (await db.Sessions.AsNoTracking().Where(s => s.ClassId == classId).OrderBy(s => s.StartsAt).ToListAsync(ct))
        .Select(s => s.ToDto()).ToList();

    public async Task<IReadOnlyList<SessionDto>> ListTeacherSessionsAsync(Guid teacherId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        (await db.Sessions.AsNoTracking()
            .Where(s => s.ActualTeacherId == teacherId && s.StartsAt >= from && s.StartsAt < to && s.Status != SessionStatus.Cancelled)
            .OrderBy(s => s.StartsAt).ToListAsync(ct))
        .Select(s => s.ToDto()).ToList();

    /// <summary>
    /// Sessions the signed-in learner (or the students linked to them) attends: those of their fixed classes except the ones
    /// they asked leave for, plus the make-up and flexible sessions they booked.
    /// </summary>
    public async Task<IReadOnlyList<SessionDto>> ListLearnerSessionsAsync(Guid userId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var students = await crm.ListStudentsOfUserAsync(userId, ct);
        if (students.Count == 0)
        {
            return [];
        }

        var studentIds = students.Select(s => s.Id).ToArray();
        var classIds = (await academic.ListClassIdsOfStudentsAsync(studentIds, ct)).ToArray();
        var bookings = await (from b in db.SessionBookings.AsNoTracking()
                              join s in db.Sessions.AsNoTracking() on b.SessionId equals s.Id
                              where studentIds.Contains(b.StudentId) && s.StartsAt >= @from && s.StartsAt < to
                              select new { b.SessionId, b.Status }).ToListAsync(ct);
        var away = bookings.Where(b => b.Status is BookingStatus.Postponed or BookingStatus.Cancelled).Select(b => b.SessionId).ToHashSet();
        var booked = bookings.Where(b => b.Status is not (BookingStatus.Postponed or BookingStatus.Cancelled)).Select(b => b.SessionId).ToArray();
        away.ExceptWith(booked);
        var sessions = await db.Sessions.AsNoTracking()
            .Where(s => (classIds.Contains(s.ClassId) || booked.Contains(s.Id)) && s.StartsAt >= from && s.StartsAt < to && s.Status != SessionStatus.Cancelled)
            .OrderBy(s => s.StartsAt).ToListAsync(ct);
        return sessions.Where(s => booked.Contains(s.Id) || !away.Contains(s.Id)).Select(s => s.ToDto()).ToList();
    }
}

public sealed record PatternView(
    Guid Id, Guid ClassId, IReadOnlyList<int> DaysOfWeek, TimeOnly StartTime, int DurationMinutes, string TimeZone, DateOnly EffectiveFrom,
    Guid? TeacherId, Guid? RoomId);

public static class SessionMapping
{
    public static SessionDto ToDto(this Session s) => new(
        s.Id, s.ClassId, s.BranchId, s.LessonId, s.SequenceNo, s.StartsAt, s.EndsAt, s.LocalDate, s.TimeZone,
        s.DeliveryMode, s.ActualTeacherId, s.RoomId, s.Status.ToString());
}
