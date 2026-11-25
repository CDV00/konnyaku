using Konnyaku.Academic.Contracts;
using Konnyaku.Application.Authorization;
using Konnyaku.Application.Persistence;
using Konnyaku.Billing.Contracts;
using Konnyaku.Crm.Contracts;
using Konnyaku.Scheduling.Contracts;
using Konnyaku.Scheduling.Domain;
using Konnyaku.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Konnyaku.Scheduling.Application;

internal static class BookingErrors
{
    public static readonly Error BookingNotFound = Error.NotFound("scheduling.booking_not_found");
    public static readonly Error EntitlementNotFound = Error.NotFound("scheduling.entitlement_not_found");
    public static readonly Error SessionFull = Error.Conflict("scheduling.session_full");
    public static readonly Error SessionStarted = Error.Conflict("scheduling.session_started");
    public static readonly Error AlreadyBooked = Error.Conflict("scheduling.already_booked");
    public static readonly Error ModeInvalid = Error.Validation("scheduling.mode_invalid");
    public static readonly Error WaitlistNotFound = Error.NotFound("scheduling.waitlist_not_found");

    public static Error Hours(string code, int hours) => Error.Conflict(code, new Dictionary<string, object?> { ["hours"] = hours });
}

public sealed record PostponeResult(Guid BookingId, Guid EntitlementId, DateTimeOffset ExpiresAt);

public sealed record SwitchModeCommand(string Mode);

/// <param name="Mode">Online or Offline; may be left out when the session offers only one.</param>
public sealed record BookSessionCommand(Guid SessionId, string? Mode);

/// <param name="StudentId">Only for an account linked to several students (a parent): whose booking.</param>
public sealed record BookFlexibleCommand(string? Mode, Guid? StudentId);

public sealed record MakeupRequestCommand(string? Note);

/// <param name="EntitlementId">Waiting to use a make-up right; without it, waiting for a session of a flexible class.</param>
public sealed record JoinWaitlistCommand(Guid? EntitlementId, string? Mode, Guid? StudentId);

/// <summary>
/// What a learner (or parent) does with their own seats: ask for leave, book a make-up, switch online/offline in a hybrid
/// session, book sessions of a flexible class, wait for a full session. Every action checks the booking belongs to one of
/// the students linked to the signed-in account.
/// </summary>
public sealed class LearnerBookingHandlers(
    ISchedulingDbContext db, ICrmApi crm, IAcademicApi academic, IBillingApi billing, SeatLedger seats, BookingSettingsStore settings,
    IDbErrorClassifier errors, TimeProvider clock)
{
    public async Task<Result<PostponeResult>> PostponeAsync(Guid userId, Guid bookingId, CancellationToken ct)
    {
        var (booking, session) = await OwnBookingAsync(userId, bookingId, ct);
        if (booking is null || session is null)
        {
            return BookingErrors.BookingNotFound;
        }

        if (booking.Kind != BookingKind.Regular || booking.Status != BookingStatus.Booked || !session.IsChangeable)
        {
            return Error.Conflict("scheduling.booking_not_postponable");
        }

        var rules = await settings.GetAsync(ct);
        var now = clock.GetUtcNow();
        if (now > session.StartsAt.AddHours(-rules.PostponeDeadlineHours))
        {
            return BookingErrors.Hours("scheduling.postpone_deadline_passed", rules.PostponeDeadlineHours);
        }

        var postponed = await db.SessionBookings.CountAsync(b => b.EnrollmentId == booking.EnrollmentId && b.Status == BookingStatus.Postponed, ct);
        if (postponed >= rules.MaxPostponesPerEnrollment)
        {
            return Error.Conflict("scheduling.postpone_limit_reached", new Dictionary<string, object?> { ["max"] = rules.MaxPostponesPerEnrollment });
        }

        booking.Postpone(now);
        var entitlement = new MakeupEntitlement(booking.StudentId, booking.EnrollmentId, booking.ClassId, session.LessonId, booking.Id, session.Id,
            now.AddDays(rules.EntitlementDays), now);
        db.Entitlements.Add(entitlement);
        db.Publish(new SessionSeatReleasedV1(session.Id));

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await seats.ReleaseAsync(session.Id, booking.Mode, false, ct);
        if (await SaveAsync(ct) is { } conflict)
        {
            return conflict;
        }

        await transaction.CommitAsync(ct);
        return new PostponeResult(booking.Id, entitlement.Id, entitlement.ExpiresAt);
    }

    /// <summary>Cancels a make-up or flexible booking before the leave deadline (a make-up right comes back).</summary>
    public async Task<Result<Guid>> CancelAsync(Guid userId, Guid bookingId, CancellationToken ct)
    {
        var (booking, session) = await OwnBookingAsync(userId, bookingId, ct);
        if (booking is null || session is null)
        {
            return BookingErrors.BookingNotFound;
        }

        if (booking.Kind is not (BookingKind.Makeup or BookingKind.Flexible) || booking.Status != BookingStatus.Booked)
        {
            return Error.Conflict("scheduling.booking_not_cancellable");
        }

        var rules = await settings.GetAsync(ct);
        var now = clock.GetUtcNow();
        if (now > session.StartsAt.AddHours(-rules.PostponeDeadlineHours))
        {
            return BookingErrors.Hours("scheduling.cancel_deadline_passed", rules.PostponeDeadlineHours);
        }

        booking.Cancel(now);
        if (booking.Kind == BookingKind.Makeup)
        {
            (await db.Entitlements.FirstOrDefaultAsync(e => e.UsedByBookingId == booking.Id, ct))?.Release();
        }

        db.Publish(new SessionSeatReleasedV1(session.Id));
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await seats.ReleaseAsync(session.Id, booking.Mode, booking.Kind == BookingKind.Makeup, ct);
        if (await SaveAsync(ct) is { } conflict)
        {
            return conflict;
        }

        await transaction.CommitAsync(ct);
        return booking.Id;
    }

    public async Task<Result<Guid>> SwitchModeAsync(Guid userId, Guid bookingId, SwitchModeCommand command, CancellationToken ct)
    {
        if (!Enum.TryParse<SeatMode>(command.Mode, out var target))
        {
            return BookingErrors.ModeInvalid;
        }

        var (booking, session) = await OwnBookingAsync(userId, bookingId, ct);
        if (booking is null || session is null)
        {
            return BookingErrors.BookingNotFound;
        }

        if (booking.Status != BookingStatus.Booked || !session.IsChangeable)
        {
            return Error.Conflict("scheduling.booking_not_changeable");
        }

        if (session.DeliveryMode != "Hybrid")
        {
            return Error.Conflict("scheduling.session_not_hybrid");
        }

        var rules = await settings.GetAsync(ct);
        var now = clock.GetUtcNow();
        if (now > session.StartsAt.AddHours(-rules.ModeSwitchDeadlineHours))
        {
            return BookingErrors.Hours("scheduling.mode_switch_deadline_passed", rules.ModeSwitchDeadlineHours);
        }

        if (booking.Mode == target)
        {
            return booking.Id;
        }

        await seats.EnsureAsync(session, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (!await seats.TrySwitchAsync(session.Id, target, ct))
        {
            return Error.Conflict("scheduling.mode_full");
        }

        booking.SwitchMode(target, now);
        db.Publish(new SessionSeatReleasedV1(session.Id));
        if (await SaveAsync(ct) is { } conflict)
        {
            return conflict;
        }

        await transaction.CommitAsync(ct);
        return booking.Id;
    }

    /// <summary>Uses a make-up right in a session of the same lesson in another class, while it has a make-up seat.</summary>
    public async Task<Result<Guid>> BookMakeupAsync(Guid userId, Guid entitlementId, BookSessionCommand command, CancellationToken ct)
    {
        var entitlement = await OwnEntitlementAsync(userId, entitlementId, ct);
        if (entitlement is null)
        {
            return BookingErrors.EntitlementNotFound;
        }

        var now = clock.GetUtcNow();
        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == command.SessionId, ct);
        if (CheckMakeupSession(entitlement, session, now) is { } invalid)
        {
            return invalid;
        }

        if (ModeFor(session!, command.Mode) is not { } mode)
        {
            return BookingErrors.ModeInvalid;
        }

        await seats.EnsureAsync(session!, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (!await seats.TryTakeAsync(session!.Id, mode, MakeupSeat.Required, ct))
        {
            return BookingErrors.SessionFull;
        }

        var booked = await PlaceAsync(session, entitlement.StudentId, entitlement.EnrollmentId, mode, BookingKind.Makeup, entitlement.SourceBookingId, now, ct);
        if (!booked.IsSuccess)
        {
            return booked.Error!;
        }

        entitlement.Use(booked.Value.Id);
        if (await SaveAsync(ct) is { } conflict)
        {
            return conflict;
        }

        await transaction.CommitAsync(ct);
        return booked.Value.Id;
    }

    /// <summary>No session of the same lesson fits: the learner asks staff to place the make-up by hand.</summary>
    public async Task<Result<Guid>> RequestMakeupAsync(Guid userId, Guid entitlementId, MakeupRequestCommand command, CancellationToken ct)
    {
        var entitlement = await OwnEntitlementAsync(userId, entitlementId, ct);
        if (entitlement is null)
        {
            return BookingErrors.EntitlementNotFound;
        }

        var now = clock.GetUtcNow();
        var open = entitlement.EnsureOpen(now);
        if (!open.IsSuccess)
        {
            return open.Error!;
        }

        if (entitlement.RequestedAt is null)
        {
            entitlement.Request(command.Note, now);
            await db.SaveChangesAsync(ct);
        }

        return entitlement.Id;
    }

    public async Task<Result<Guid>> BookFlexibleAsync(Guid userId, Guid sessionId, BookFlexibleCommand command, CancellationToken ct)
    {
        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null)
        {
            return SchedulingErrors.SessionNotFound;
        }

        var cls = await academic.GetClassAsync(session.ClassId, ct);
        if (cls?.BookingMode != "Flexible")
        {
            return Error.Conflict("scheduling.class_not_flexible");
        }

        var now = clock.GetUtcNow();
        if (!session.IsChangeable || session.StartsAt <= now)
        {
            return BookingErrors.SessionStarted;
        }

        var enrollment = await OwnEnrollmentAsync(userId, session.ClassId, command.StudentId, ct);
        if (enrollment is null)
        {
            return Error.Forbidden("scheduling.not_enrolled");
        }

        if (enrollment.Status != "Active")
        {
            return Error.Conflict("scheduling.enrollment_not_active");
        }

        if (ModeFor(session, command.Mode) is not { } mode)
        {
            return BookingErrors.ModeInvalid;
        }

        // A package limits how many sessions may be booked ahead (each confirmed attendance spends one lesson).
        var account = (await billing.ListCreditAccountsAsync([enrollment.StudentId], ct)).FirstOrDefault(a => a.EnrollmentId == enrollment.Id);
        if (account is not null)
        {
            var ahead = await (from b in db.SessionBookings
                               join s in db.Sessions on b.SessionId equals s.Id
                               where b.EnrollmentId == enrollment.Id && b.Status == BookingStatus.Booked && s.StartsAt > now
                               select b.Id).CountAsync(ct);
            if (ahead + 1 > account.Total)
            {
                return Error.Conflict("scheduling.no_credits_left", new Dictionary<string, object?> { ["credits"] = account.Total });
            }
        }

        await seats.EnsureAsync(session, ct, cls);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (!await seats.TryTakeAsync(session.Id, mode, MakeupSeat.None, ct))
        {
            return BookingErrors.SessionFull;
        }

        var booked = await PlaceAsync(session, enrollment.StudentId, enrollment.Id, mode, BookingKind.Flexible, null, now, ct);
        if (!booked.IsSuccess)
        {
            return booked.Error!;
        }

        if (await SaveAsync(ct) is { } conflict)
        {
            return conflict;
        }

        await transaction.CommitAsync(ct);
        return booked.Value.Id;
    }

    /// <summary>Waits for a full session; the Worker offers a freed seat to the first in line and holds it for a while.</summary>
    public async Task<Result<Guid>> JoinWaitlistAsync(Guid userId, Guid sessionId, JoinWaitlistCommand command, CancellationToken ct)
    {
        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null)
        {
            return SchedulingErrors.SessionNotFound;
        }

        var now = clock.GetUtcNow();
        if (ModeFor(session, command.Mode) is not { } mode)
        {
            return BookingErrors.ModeInvalid;
        }

        SessionWaitlistEntry entry;
        if (command.EntitlementId is { } entitlementId)
        {
            var entitlement = await OwnEntitlementAsync(userId, entitlementId, ct);
            if (entitlement is null)
            {
                return BookingErrors.EntitlementNotFound;
            }

            if (CheckMakeupSession(entitlement, session, now) is { } invalid)
            {
                return invalid;
            }

            entry = new SessionWaitlistEntry(session.Id, entitlement.StudentId, entitlement.EnrollmentId, BookingKind.Makeup, mode, entitlement.Id, now);
        }
        else
        {
            if ((await academic.GetClassAsync(session.ClassId, ct))?.BookingMode != "Flexible")
            {
                return Error.Conflict("scheduling.class_not_flexible");
            }

            if (!session.IsChangeable || session.StartsAt <= now)
            {
                return BookingErrors.SessionStarted;
            }

            var enrollment = await OwnEnrollmentAsync(userId, session.ClassId, command.StudentId, ct);
            if (enrollment is null)
            {
                return Error.Forbidden("scheduling.not_enrolled");
            }

            entry = new SessionWaitlistEntry(session.Id, enrollment.StudentId, enrollment.Id, BookingKind.Flexible, mode, null, now);
        }

        if (await db.SessionBookings.AnyAsync(b => b.SessionId == session.Id && b.StudentId == entry.StudentId && b.Status == BookingStatus.Booked, ct))
        {
            return BookingErrors.AlreadyBooked;
        }

        var seat = await seats.EnsureAsync(session, ct);
        if (seat.Free(mode) > 0 && (entry.Kind != BookingKind.Makeup || seat.FreeMakeup > 0))
        {
            return Error.Conflict("scheduling.seat_available");
        }

        db.Waitlist.Add(entry);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (errors.IsUniqueViolation(ex))
        {
            return Error.Conflict("scheduling.already_waitlisted");
        }

        return entry.Id;
    }

    /// <summary>Takes the seat held for the learner: the booking is made without taking another seat.</summary>
    public async Task<Result<Guid>> AcceptOfferAsync(Guid userId, Guid entryId, CancellationToken ct)
    {
        var entry = await OwnWaitlistAsync(userId, entryId, ct);
        if (entry is null)
        {
            return BookingErrors.WaitlistNotFound;
        }

        var now = clock.GetUtcNow();
        if (entry.Status != WaitlistStatus.Offered || entry.OfferExpiresAt <= now)
        {
            return Error.Conflict("scheduling.offer_expired");
        }

        var session = await db.Sessions.FirstAsync(s => s.Id == entry.SessionId, ct);
        MakeupEntitlement? entitlement = null;
        if (entry.EntitlementId is { } entitlementId)
        {
            entitlement = await db.Entitlements.FirstAsync(e => e.Id == entitlementId, ct);
            var open = entitlement.EnsureOpen(now);
            if (!open.IsSuccess)
            {
                return open.Error!;
            }
        }

        var booked = await PlaceAsync(session, entry.StudentId, entry.EnrollmentId, entry.Mode, entry.Kind, entitlement?.SourceBookingId, now, ct);
        if (!booked.IsSuccess)
        {
            return booked.Error!;
        }

        entitlement?.Use(booked.Value.Id);
        entry.Accept();
        return await SaveAsync(ct) is { } conflict ? conflict : booked.Value.Id;
    }

    public async Task<Result<Guid>> WithdrawAsync(Guid userId, Guid entryId, CancellationToken ct)
    {
        var entry = await OwnWaitlistAsync(userId, entryId, ct);
        if (entry is null || !entry.IsActive)
        {
            return BookingErrors.WaitlistNotFound;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (entry.Status == WaitlistStatus.Offered)
        {
            await seats.ReleaseAsync(entry.SessionId, entry.Mode, entry.Kind == BookingKind.Makeup, ct);
            db.Publish(new SessionSeatReleasedV1(entry.SessionId));
        }

        entry.Withdraw();
        if (await SaveAsync(ct) is { } conflict)
        {
            return conflict;
        }

        await transaction.CommitAsync(ct);
        return entry.Id;
    }

    /// <summary>Why the session cannot host this make-up, or null.</summary>
    internal static Error? CheckMakeupSession(MakeupEntitlement entitlement, Session? session, DateTimeOffset now)
    {
        if (session is null)
        {
            return SchedulingErrors.SessionNotFound;
        }

        if (entitlement.EnsureOpen(now) is { IsSuccess: false } closed)
        {
            return closed.Error;
        }

        if (entitlement.LessonId is null || session.LessonId != entitlement.LessonId || session.ClassId == entitlement.ClassId)
        {
            return Error.Validation("scheduling.makeup_lesson_mismatch");
        }

        if (!session.IsChangeable || session.StartsAt <= now)
        {
            return BookingErrors.SessionStarted;
        }

        return session.StartsAt > entitlement.ExpiresAt ? Error.Conflict("scheduling.makeup_after_expiry") : null;
    }

    /// <summary>The seat mode asked for, when the session offers it (a single-mode session needs no choice).</summary>
    internal static SeatMode? ModeFor(Session session, string? requested)
    {
        var modes = SeatLedger.ModesOf(session.DeliveryMode);
        if (string.IsNullOrWhiteSpace(requested))
        {
            return modes.Count == 1 ? modes[0] : null;
        }

        return Enum.TryParse<SeatMode>(requested, out var mode) && modes.Contains(mode) ? mode : null;
    }

    /// <summary>A booking row for the learner in the session: new, or a released one booked again.</summary>
    internal static async Task<Result<SessionBooking>> PlaceAsync(ISchedulingDbContext db, Session session, Guid studentId, Guid enrollmentId,
        SeatMode mode, BookingKind kind, Guid? sourceBookingId, DateTimeOffset now, CancellationToken ct)
    {
        var existing = await db.SessionBookings.FirstOrDefaultAsync(b => b.SessionId == session.Id && b.StudentId == studentId, ct);
        if (existing is null)
        {
            var booking = new SessionBooking(session.Id, session.ClassId, studentId, enrollmentId, mode, kind, sourceBookingId, now);
            db.SessionBookings.Add(booking);
            return booking;
        }

        if (existing.Status is not BookingStatus.Cancelled)
        {
            return BookingErrors.AlreadyBooked;
        }

        existing.Rebook(enrollmentId, mode, kind, sourceBookingId, now);
        return existing;
    }

    private Task<Result<SessionBooking>> PlaceAsync(Session session, Guid studentId, Guid enrollmentId, SeatMode mode, BookingKind kind,
        Guid? sourceBookingId, DateTimeOffset now, CancellationToken ct) =>
        PlaceAsync(db, session, studentId, enrollmentId, mode, kind, sourceBookingId, now, ct);

    private async Task<HashSet<Guid>> StudentIdsAsync(Guid userId, CancellationToken ct) =>
        (await crm.ListStudentsOfUserAsync(userId, ct)).Select(s => s.Id).ToHashSet();

    private async Task<(SessionBooking? Booking, Session? Session)> OwnBookingAsync(Guid userId, Guid bookingId, CancellationToken ct)
    {
        var booking = await db.SessionBookings.FirstOrDefaultAsync(b => b.Id == bookingId, ct);
        if (booking is null || !(await StudentIdsAsync(userId, ct)).Contains(booking.StudentId))
        {
            return (null, null);
        }

        return (booking, await db.Sessions.FirstOrDefaultAsync(s => s.Id == booking.SessionId, ct));
    }

    private async Task<MakeupEntitlement?> OwnEntitlementAsync(Guid userId, Guid entitlementId, CancellationToken ct)
    {
        var entitlement = await db.Entitlements.FirstOrDefaultAsync(e => e.Id == entitlementId, ct);
        return entitlement is not null && (await StudentIdsAsync(userId, ct)).Contains(entitlement.StudentId) ? entitlement : null;
    }

    private async Task<SessionWaitlistEntry?> OwnWaitlistAsync(Guid userId, Guid entryId, CancellationToken ct)
    {
        var entry = await db.Waitlist.FirstOrDefaultAsync(w => w.Id == entryId, ct);
        return entry is not null && (await StudentIdsAsync(userId, ct)).Contains(entry.StudentId) ? entry : null;
    }

    private async Task<EnrollmentDto?> OwnEnrollmentAsync(Guid userId, Guid classId, Guid? studentId, CancellationToken ct)
    {
        var students = await StudentIdsAsync(userId, ct);
        if (studentId is { } id && !students.Contains(id))
        {
            return null;
        }

        var enrollments = await academic.ListEnrollmentsOfStudentsAsync(studentId is { } only ? [only] : students, ct);
        return enrollments.FirstOrDefault(e => e.ClassId == classId);
    }

    private async Task<Error?> SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("scheduling.booking_changed_concurrently");
        }
        catch (DbUpdateException ex) when (errors.IsUniqueViolation(ex))
        {
            return BookingErrors.AlreadyBooked;
        }
    }
}

public sealed record PlaceMakeupCommand(Guid SessionId, string? Mode);

public sealed record BookingSettingsView(
    int PostponeDeadlineHours, int MaxPostponesPerEnrollment, int EntitlementDays, int ModeSwitchDeadlineHours, int MakeupSeatsPerSession,
    int WaitlistHoldHours);

/// <summary>Staff side of make-ups: place an exceptional make-up by hand, decline a request, and the center's booking rules.</summary>
public sealed class MakeupAdminHandlers(
    ISchedulingDbContext db, IAccessControl access, SeatLedger seats, IAcademicApi academic, IDbErrorClassifier errors, TimeProvider clock)
{
    /// <summary>
    /// Places a make-up in any coming session (another lesson, after the right expired, beyond the make-up seats): the
    /// exception staff approve. The seat itself must still be free.
    /// </summary>
    public async Task<Result<Guid>> PlaceAsync(Guid staffId, Guid entitlementId, PlaceMakeupCommand command, CancellationToken ct)
    {
        var entitlement = await db.Entitlements.FirstOrDefaultAsync(e => e.Id == entitlementId, ct);
        if (entitlement is null || !await CanManageAsync(entitlement, ct))
        {
            return BookingErrors.EntitlementNotFound;
        }

        if (entitlement.UsedByBookingId is not null)
        {
            return Error.Conflict("scheduling.entitlement_used");
        }

        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == command.SessionId, ct);
        if (session is null)
        {
            return SchedulingErrors.SessionNotFound;
        }

        if (await access.EnsureAsync(Permissions.Schedules, ct, session.BranchId) is { } forbidden)
        {
            return forbidden;
        }

        var now = clock.GetUtcNow();
        if (!session.IsChangeable || session.StartsAt <= now)
        {
            return BookingErrors.SessionStarted;
        }

        if (session.ClassId == entitlement.ClassId)
        {
            return Error.Validation("scheduling.makeup_lesson_mismatch");
        }

        if (LearnerBookingHandlers.ModeFor(session, command.Mode) is not { } mode)
        {
            return BookingErrors.ModeInvalid;
        }

        await seats.EnsureAsync(session, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (!await seats.TryTakeAsync(session.Id, mode, MakeupSeat.Counted, ct))
        {
            return BookingErrors.SessionFull;
        }

        var booked = await LearnerBookingHandlers.PlaceAsync(db, session, entitlement.StudentId, entitlement.EnrollmentId, mode, BookingKind.Makeup,
            entitlement.SourceBookingId, now, ct);
        if (!booked.IsSuccess)
        {
            return booked.Error!;
        }

        booked.Value.PlacedByStaff(staffId);
        entitlement.Use(booked.Value.Id);
        db.Publish(new MakeupApprovedV1(booked.Value.Id, entitlement.Id, session.Id, entitlement.StudentId));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("scheduling.entitlement_used");
        }
        catch (DbUpdateException ex) when (errors.IsUniqueViolation(ex))
        {
            return BookingErrors.AlreadyBooked;
        }

        await transaction.CommitAsync(ct);
        return booked.Value.Id;
    }

    public async Task<Result<Guid>> DeclineAsync(Guid entitlementId, CancellationToken ct)
    {
        var entitlement = await db.Entitlements.FirstOrDefaultAsync(e => e.Id == entitlementId, ct);
        if (entitlement is null || !await CanManageAsync(entitlement, ct))
        {
            return BookingErrors.EntitlementNotFound;
        }

        if (entitlement.UsedByBookingId is not null)
        {
            return Error.Conflict("scheduling.entitlement_used");
        }

        entitlement.Decline(clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return entitlement.Id;
    }

    public async Task<BookingSettingsView> GetSettingsAsync(CancellationToken ct) =>
        (await db.Settings.AsNoTracking().FirstOrDefaultAsync(ct) ?? BookingSettings.Defaults).ToView();

    public async Task<Result<BookingSettingsView>> SetSettingsAsync(BookingSettingsView command, CancellationToken ct)
    {
        if (!BookingSettings.IsValid(command.PostponeDeadlineHours, command.MaxPostponesPerEnrollment, command.EntitlementDays,
                command.ModeSwitchDeadlineHours, command.MakeupSeatsPerSession, command.WaitlistHoldHours))
        {
            return Error.Validation("scheduling.settings_invalid");
        }

        var row = await db.Settings.FirstOrDefaultAsync(ct);
        if (row is null)
        {
            row = new BookingSettings(command.PostponeDeadlineHours, command.MaxPostponesPerEnrollment, command.EntitlementDays,
                command.ModeSwitchDeadlineHours, command.MakeupSeatsPerSession, command.WaitlistHoldHours);
            db.Settings.Add(row);
        }
        else
        {
            row.Change(command.PostponeDeadlineHours, command.MaxPostponesPerEnrollment, command.EntitlementDays, command.ModeSwitchDeadlineHours,
                command.MakeupSeatsPerSession, command.WaitlistHoldHours);
        }

        await db.SaveChangesAsync(ct);
        return row.ToView();
    }

    /// <summary>Staff act on make-ups of the classes of their branches.</summary>
    private async Task<bool> CanManageAsync(MakeupEntitlement entitlement, CancellationToken ct) =>
        await academic.GetClassAsync(entitlement.ClassId, ct) is { } cls && await access.CanAsync(Permissions.Schedules, cls.BranchId, ct);
}

public static class BookingMapping
{
    public static BookingSettingsView ToView(this BookingSettings s) => new(
        s.PostponeDeadlineHours, s.MaxPostponesPerEnrollment, s.EntitlementDays, s.ModeSwitchDeadlineHours, s.MakeupSeatsPerSession, s.WaitlistHoldHours);
}
