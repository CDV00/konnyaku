using Konnyaku.Academic.Contracts;
using Konnyaku.Organization.Contracts;
using Konnyaku.Scheduling.Contracts;
using Konnyaku.Scheduling.Domain;
using Microsoft.EntityFrameworkCore;

namespace Konnyaku.Scheduling.Application;

/// <summary>The center's booking rules, or the safe defaults when it never changed them.</summary>
public sealed class BookingSettingsStore(ISchedulingDbContext db)
{
    public async Task<BookingSettings> GetAsync(CancellationToken ct) =>
        await db.Settings.AsNoTracking().FirstOrDefaultAsync(ct) ?? BookingSettings.Defaults;
}

/// <summary>Whether a seat change also counts against the make-up seats of the session.</summary>
public enum MakeupSeat
{
    None,

    /// <summary>Counted, even beyond the make-up capacity (staff placing an exception).</summary>
    Counted,

    /// <summary>Counted, and refused when the make-up seats are all taken.</summary>
    Required,
}

/// <summary>
/// Seat counters of sessions. Every change is one conditional UPDATE, so concurrent requests can never take more seats
/// than exist (the same technique as the class seat in Academic's enrollment). Callers that also write a booking run
/// both in one transaction.
/// </summary>
public sealed class SeatLedger(
    ISchedulingDbContext db, IAcademicApi academic, IOrganizationApi organization, BookingSettingsStore settings)
{
    /// <summary>The seat row of a session, created from the class size, room and center rules when missing.</summary>
    public async Task<SessionSeat> EnsureAsync(Session session, CancellationToken ct, ClassDto? cls = null)
    {
        var seat = await db.Seats.AsNoTracking().FirstOrDefaultAsync(s => s.SessionId == session.Id, ct);
        if (seat is not null)
        {
            return seat;
        }

        var (online, offline, makeup) = await CapacityAsync(session, cls, ct);
        // Insert-if-missing without touching the caller's tracked changes; a concurrent creator simply wins.
        await db.InsertSeatIfMissingAsync(new SessionSeat(session.Id, online, offline, makeup), ct);
        return await db.Seats.AsNoTracking().FirstAsync(s => s.SessionId == session.Id, ct);
    }

    /// <summary>Capacities from the class size plus make-up seats; offline never more than the room holds.</summary>
    public async Task<(int Online, int Offline, int Makeup)> CapacityAsync(Session session, ClassDto? cls, CancellationToken ct)
    {
        cls ??= await academic.GetClassAsync(session.ClassId, ct);
        var rules = await settings.GetAsync(ct);
        int? room = session.RoomId is { } roomId ? (await organization.GetRoomAsync(roomId, ct))?.Capacity : null;
        var makeup = cls?.BookingMode == "Flexible" ? 0 : rules.MakeupSeatsPerSession;
        var (online, offline) = SessionSeat.CapacityFor(session.DeliveryMode, cls?.Capacity ?? 0, makeup, room);
        return (online, offline, makeup);
    }

    /// <summary>Takes a seat if one is free (and a make-up seat when required). False when full.</summary>
    public async Task<bool> TryTakeAsync(Guid sessionId, SeatMode mode, MakeupSeat makeup, CancellationToken ct)
    {
        var on = mode == SeatMode.Online ? 1 : 0;
        var off = 1 - on;
        var mk = makeup == MakeupSeat.None ? 0 : 1;
        var requireMakeup = makeup == MakeupSeat.Required;
        return await db.Seats
            .Where(s => s.SessionId == sessionId
                        && (on == 0 || s.OnlineBooked < s.OnlineCapacity)
                        && (off == 0 || s.OfflineBooked < s.OfflineCapacity)
                        && (!requireMakeup || s.MakeupBooked < s.MakeupCapacity))
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.OnlineBooked, s => s.OnlineBooked + on)
                .SetProperty(s => s.OfflineBooked, s => s.OfflineBooked + off)
                .SetProperty(s => s.MakeupBooked, s => s.MakeupBooked + mk), ct) > 0;
    }

    /// <summary>Takes a seat even beyond capacity: a learner of the class keeps their place (the enrollment reserved it).</summary>
    public async Task TakeAsync(Guid sessionId, SeatMode mode, CancellationToken ct)
    {
        var on = mode == SeatMode.Online ? 1 : 0;
        var off = 1 - on;
        await db.Seats.Where(s => s.SessionId == sessionId)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.OnlineBooked, s => s.OnlineBooked + on)
                .SetProperty(s => s.OfflineBooked, s => s.OfflineBooked + off), ct);
    }

    public async Task ReleaseAsync(Guid sessionId, SeatMode mode, bool makeup, CancellationToken ct)
    {
        var on = mode == SeatMode.Online ? 1 : 0;
        var off = 1 - on;
        var mk = makeup ? 1 : 0;
        await db.Seats.Where(s => s.SessionId == sessionId)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.OnlineBooked, s => s.OnlineBooked >= on ? s.OnlineBooked - on : 0)
                .SetProperty(s => s.OfflineBooked, s => s.OfflineBooked >= off ? s.OfflineBooked - off : 0)
                .SetProperty(s => s.MakeupBooked, s => s.MakeupBooked >= mk ? s.MakeupBooked - mk : 0), ct);
    }

    /// <summary>Moves a seat to the other mode if that mode has room. False when full.</summary>
    public async Task<bool> TrySwitchAsync(Guid sessionId, SeatMode to, CancellationToken ct)
    {
        var on = to == SeatMode.Online ? 1 : 0;
        var off = 1 - on;
        return await db.Seats
            .Where(s => s.SessionId == sessionId
                        && (on == 0 || s.OnlineBooked < s.OnlineCapacity)
                        && (off == 0 || s.OfflineBooked < s.OfflineCapacity))
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.OnlineBooked, s => on == 1 ? s.OnlineBooked + 1 : (s.OnlineBooked > 0 ? s.OnlineBooked - 1 : 0))
                .SetProperty(s => s.OfflineBooked, s => off == 1 ? s.OfflineBooked + 1 : (s.OfflineBooked > 0 ? s.OfflineBooked - 1 : 0)), ct) > 0;
    }

    /// <summary>Recounts taken seats from bookings and held offers (after bulk changes such as a new delivery mode).</summary>
    public async Task RecountAsync(IReadOnlyCollection<Guid> sessionIds, CancellationToken ct)
    {
        var ids = sessionIds.ToArray();
        await db.Seats.Where(s => ids.Contains(s.SessionId))
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.OnlineBooked, s =>
                    db.SessionBookings.Count(b => b.SessionId == s.SessionId && b.Status == BookingStatus.Booked && b.Mode == SeatMode.Online)
                    + db.Waitlist.Count(w => w.SessionId == s.SessionId && w.Status == WaitlistStatus.Offered && w.Mode == SeatMode.Online))
                .SetProperty(s => s.OfflineBooked, s =>
                    db.SessionBookings.Count(b => b.SessionId == s.SessionId && b.Status == BookingStatus.Booked && b.Mode == SeatMode.Offline)
                    + db.Waitlist.Count(w => w.SessionId == s.SessionId && w.Status == WaitlistStatus.Offered && w.Mode == SeatMode.Offline))
                .SetProperty(s => s.MakeupBooked, s =>
                    db.SessionBookings.Count(b => b.SessionId == s.SessionId && b.Status == BookingStatus.Booked && b.Kind == BookingKind.Makeup)
                    + db.Waitlist.Count(w => w.SessionId == s.SessionId && w.Status == WaitlistStatus.Offered && w.Kind == BookingKind.Makeup)), ct);
    }

    /// <summary>The seat modes a session offers.</summary>
    public static IReadOnlyList<SeatMode> ModesOf(string deliveryMode) => deliveryMode switch
    {
        "Online" => [SeatMode.Online],
        "Offline" => [SeatMode.Offline],
        _ => [SeatMode.Offline, SeatMode.Online],
    };
}

/// <summary>
/// Keeps the Regular bookings of fixed classes in step with enrollments: every coming session of the class gets a seat
/// for each active enrollment, except during a freeze; a dropped enrollment releases all its coming seats (make-ups too).
/// Idempotent, so it runs again safely from every trigger (enrollment events, scheduling, re-planning).
/// </summary>
public sealed class BookingSync(ISchedulingDbContext db, IAcademicApi academic, SeatLedger seats, TimeProvider clock)
{
    /// <summary>Regular bookings of all enrollments of a class (<paramref name="sessionId"/>: of that session only, even past).</summary>
    public async Task SyncClassAsync(Guid classId, CancellationToken ct, Guid? sessionId = null)
    {
        var cls = await academic.GetClassAsync(classId, ct);
        if (cls is null)
        {
            return;
        }

        var sessions = await SessionsAsync(classId, sessionId, ct);
        await SyncAsync(cls, sessions, await academic.ListEnrollmentsAsync(classId, ct), ct);
    }

    public async Task SyncEnrollmentAsync(Guid enrollmentId, CancellationToken ct)
    {
        var enrollment = await academic.GetEnrollmentAsync(enrollmentId, ct);
        if (enrollment is null)
        {
            return;
        }

        if (enrollment.Status is not ("Active" or "Frozen"))
        {
            await ReleaseEnrollmentAsync(enrollmentId, ct);
            return;
        }

        var cls = await academic.GetClassAsync(enrollment.ClassId, ct);
        if (cls is not null)
        {
            await SyncAsync(cls, await SessionsAsync(cls.Id, null, ct), [enrollment], ct);
        }
    }

    /// <summary>The enrollment ended: its coming seats (own class, make-ups, flexible sessions) and waitlist places are freed.</summary>
    public async Task ReleaseEnrollmentAsync(Guid enrollmentId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var bookings = await (from b in db.SessionBookings
                              join s in db.Sessions on b.SessionId equals s.Id
                              where b.EnrollmentId == enrollmentId && b.Status == BookingStatus.Booked && s.StartsAt > now
                              select b).ToListAsync(ct);
        var waiting = await db.Waitlist
            .Where(w => w.EnrollmentId == enrollmentId && (w.Status == WaitlistStatus.Waiting || w.Status == WaitlistStatus.Offered))
            .ToListAsync(ct);
        if (bookings.Count == 0 && waiting.Count == 0)
        {
            return;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var released = new HashSet<Guid>();
        foreach (var booking in bookings)
        {
            booking.Cancel(now);
            await seats.ReleaseAsync(booking.SessionId, booking.Mode, booking.Kind == BookingKind.Makeup, ct);
            released.Add(booking.SessionId);
        }

        foreach (var entry in waiting)
        {
            if (entry.Status == WaitlistStatus.Offered)
            {
                await seats.ReleaseAsync(entry.SessionId, entry.Mode, entry.Kind == BookingKind.Makeup, ct);
                released.Add(entry.SessionId);
            }

            entry.Withdraw();
        }

        foreach (var sessionId in released)
        {
            db.Publish(new SessionSeatReleasedV1(sessionId));
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>Sessions that will not take place: their bookings are cancelled and make-up rights return to the learners.</summary>
    public async Task ReleaseSessionsAsync(IReadOnlyCollection<Guid> sessionIds, CancellationToken ct)
    {
        var ids = sessionIds.ToArray();
        var now = clock.GetUtcNow();
        var bookings = await db.SessionBookings.Where(b => ids.Contains(b.SessionId) && b.Status == BookingStatus.Booked).ToListAsync(ct);
        var makeupIds = bookings.Where(b => b.Kind == BookingKind.Makeup).Select(b => (Guid?)b.Id).ToArray();
        foreach (var entitlement in await db.Entitlements.Where(e => makeupIds.Contains(e.UsedByBookingId)).ToListAsync(ct))
        {
            entitlement.Release();
        }

        foreach (var booking in bookings)
        {
            booking.Cancel(now);
        }

        foreach (var entry in await db.Waitlist
                     .Where(w => ids.Contains(w.SessionId) && (w.Status == WaitlistStatus.Waiting || w.Status == WaitlistStatus.Offered))
                     .ToListAsync(ct))
        {
            entry.Withdraw();
        }
    }

    /// <summary>New capacities of sessions whose room, class size or delivery mode may have changed.</summary>
    public async Task ResizeSeatsAsync(IReadOnlyCollection<Session> sessions, CancellationToken ct)
    {
        if (sessions.Count == 0)
        {
            return;
        }

        var cls = await academic.GetClassAsync(sessions.First().ClassId, ct);
        var ids = sessions.Select(s => s.Id).ToArray();
        var rows = await db.Seats.Where(s => ids.Contains(s.SessionId)).ToListAsync(ct);
        foreach (var seat in rows)
        {
            var (online, offline, makeup) = await seats.CapacityAsync(sessions.First(s => s.Id == seat.SessionId), cls, ct);
            seat.Resize(online, offline, makeup);
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task<List<Session>> SessionsAsync(Guid classId, Guid? sessionId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var query = db.Sessions.Where(s => s.ClassId == classId && s.Status != SessionStatus.Cancelled);
        query = sessionId is { } id ? query.Where(s => s.Id == id) : query.Where(s => s.StartsAt > now);
        return await query.OrderBy(s => s.StartsAt).ToListAsync(ct);
    }

    private async Task SyncAsync(ClassDto cls, IReadOnlyList<Session> sessions, IReadOnlyList<EnrollmentDto> enrollments, CancellationToken ct)
    {
        if (sessions.Count == 0)
        {
            return;
        }

        foreach (var session in sessions)
        {
            await seats.EnsureAsync(session, ct, cls);
        }

        // Flexible classes have no fixed roster: learners book each session themselves.
        if (cls.BookingMode == "Flexible" || enrollments.Count == 0)
        {
            return;
        }

        var now = clock.GetUtcNow();
        var sessionIds = sessions.Select(s => s.Id).ToArray();
        var studentIds = enrollments.Select(e => e.StudentId).ToArray();
        var existing = await db.SessionBookings
            .Where(b => sessionIds.Contains(b.SessionId) && studentIds.Contains(b.StudentId))
            .ToListAsync(ct);

        var ownTransaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        try
        {
            var released = new HashSet<Guid>();
            foreach (var enrollment in enrollments)
            {
                foreach (var session in sessions)
                {
                    var wanted = !IsFrozenOn(enrollment, session.LocalDate);
                    var booking = existing.FirstOrDefault(b => b.SessionId == session.Id && b.StudentId == enrollment.StudentId);
                    if (wanted && booking is null)
                    {
                        var mode = await TakeRegularSeatAsync(session, enrollment.PreferredMode, ct);
                        db.SessionBookings.Add(new SessionBooking(session.Id, session.ClassId, enrollment.StudentId, enrollment.Id, mode,
                            BookingKind.Regular, null, now));
                    }
                    else if (wanted && booking is { Status: BookingStatus.Cancelled, Kind: BookingKind.Regular })
                    {
                        booking.Rebook(enrollment.Id, await TakeRegularSeatAsync(session, enrollment.PreferredMode, ct), BookingKind.Regular, null, now);
                    }
                    else if (!wanted && booking is { Status: BookingStatus.Booked, Kind: BookingKind.Regular })
                    {
                        booking.Cancel(now);
                        await seats.ReleaseAsync(session.Id, booking.Mode, false, ct);
                        released.Add(session.Id);
                    }
                }
            }

            foreach (var sessionId in released)
            {
                db.Publish(new SessionSeatReleasedV1(sessionId));
            }

            await db.SaveChangesAsync(ct);
            if (ownTransaction is not null)
            {
                await ownTransaction.CommitAsync(ct);
            }
        }
        finally
        {
            if (ownTransaction is not null)
            {
                await ownTransaction.DisposeAsync();
            }
        }
    }

    private static bool IsFrozenOn(EnrollmentDto enrollment, DateOnly date) =>
        enrollment.Status == "Frozen" && enrollment.FrozenFrom is { } from && enrollment.FrozenTo is { } to && date >= from && date <= to;

    /// <summary>
    /// A learner of the class always gets a seat. In a hybrid session: the preferred mode while it has room, else the
    /// classroom while it has room, else online.
    /// </summary>
    private async Task<SeatMode> TakeRegularSeatAsync(Session session, string? preferred, CancellationToken ct)
    {
        var modes = SeatLedger.ModesOf(session.DeliveryMode).ToList();
        if (Enum.TryParse<SeatMode>(preferred, out var wanted) && modes.Remove(wanted))
        {
            modes.Insert(0, wanted);
        }

        foreach (var mode in modes)
        {
            if (await seats.TryTakeAsync(session.Id, mode, MakeupSeat.None, ct))
            {
                return mode;
            }
        }

        await seats.TakeAsync(session.Id, modes[0], ct);
        return modes[0];
    }
}
