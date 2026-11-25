using Konnyaku.Academic.Contracts;
using Konnyaku.Application.Authorization;
using Konnyaku.Billing.Contracts;
using Konnyaku.Crm.Contracts;
using Konnyaku.Scheduling.Domain;
using Microsoft.EntityFrameworkCore;

namespace Konnyaku.Scheduling.Application;

/// <summary>A learner's seat with what they may still do with it (deadlines computed from the center rules).</summary>
public sealed record MyBookingView(
    Guid Id, Guid SessionId, Guid ClassId, string ClassCode, Guid StudentId, string StudentName, int SequenceNo, DateTimeOffset StartsAt,
    DateTimeOffset EndsAt, string DeliveryMode, string SessionStatus, string Mode, string Kind, string Status, DateTimeOffset PostponeDeadline,
    DateTimeOffset ModeSwitchDeadline, bool CanPostpone, bool CanCancel, bool CanSwitchMode, int OtherModeFree);

public sealed record MakeupUseView(Guid BookingId, Guid SessionId, string ClassCode, DateTimeOffset StartsAt, string Mode);

/// <summary><see cref="State"/>: Open, Used, Expired or Declined.</summary>
public sealed record MyEntitlementView(
    Guid Id, Guid StudentId, string StudentName, Guid ClassId, string ClassCode, int SequenceNo, DateTimeOffset SourceStartsAt,
    DateTimeOffset ExpiresAt, string State, DateTimeOffset? RequestedAt, MakeupUseView? UsedBy);

public sealed record MakeupOptionView(
    Guid SessionId, Guid ClassId, string ClassCode, int SequenceNo, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string DeliveryMode,
    int OnlineFree, int OfflineFree, int MakeupFree, bool SameLesson);

public sealed record MyWaitlistView(
    Guid Id, Guid SessionId, string ClassCode, int SequenceNo, DateTimeOffset StartsAt, string Kind, string Mode, string Status,
    DateTimeOffset? OfferExpiresAt);

public sealed record FlexibleClassView(
    Guid ClassId, string ClassCode, Guid StudentId, string StudentName, Guid EnrollmentId, decimal? CreditsLeft, int BookedAhead);

public sealed record FlexibleSessionView(
    Guid SessionId, Guid ClassId, int SequenceNo, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string DeliveryMode, int OnlineFree,
    int OfflineFree, Guid? BookingId, string? BookingStatus, string? BookingMode, bool Waitlisted);

public sealed record FlexibleView(IReadOnlyList<FlexibleClassView> Classes, IReadOnlyList<FlexibleSessionView> Sessions);

public sealed record MakeupRequestView(
    Guid Id, Guid StudentId, string StudentName, Guid ClassId, string ClassCode, Guid BranchId, int SequenceNo, DateTimeOffset SourceStartsAt,
    DateTimeOffset ExpiresAt, DateTimeOffset RequestedAt, string? Note);

public sealed record FrequentPostponeView(
    Guid StudentId, string StudentName, Guid EnrollmentId, Guid ClassId, string ClassCode, int Postponed, DateTimeOffset LastPostponedAt);

/// <summary>Reads of the booking screens: the learner's schedule, make-up rights and options, flexible classes, and staff lists.</summary>
public sealed class BookingQueries(
    ISchedulingDbContext db, ICrmApi crm, IAcademicApi academic, IBillingApi billing, IAccessControl access, SeatLedger seats, BookingSync sync,
    BookingSettingsStore settings, TimeProvider clock)
{
    public const int FlexibleDaysAhead = 28;

    public async Task<IReadOnlyList<MyBookingView>> ListMyBookingsAsync(Guid userId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var students = await crm.ListStudentsOfUserAsync(userId, ct);
        if (students.Count == 0)
        {
            return [];
        }

        var ids = students.Select(s => s.Id).ToArray();
        await BackfillAsync(ids, from, to, ct);

        var rows = await (from b in db.SessionBookings.AsNoTracking()
                          join s in db.Sessions.AsNoTracking() on b.SessionId equals s.Id
                          join seat in db.Seats.AsNoTracking() on s.Id equals seat.SessionId into seatJoin
                          from seat in seatJoin.DefaultIfEmpty()
                          where ids.Contains(b.StudentId) && b.Status != BookingStatus.Cancelled && s.StartsAt >= @from && s.StartsAt < to
                          orderby s.StartsAt
                          select new { Booking = b, Session = s, Seat = seat }).ToListAsync(ct);

        var rules = await settings.GetAsync(ct);
        var now = clock.GetUtcNow();
        var codes = await ClassCodesAsync(rows.Select(r => r.Session.ClassId), ct);
        var names = students.ToDictionary(s => s.Id, s => s.FullName);
        return rows.Select(r =>
        {
            var b = r.Booking;
            var s = r.Session;
            var postponeBy = s.StartsAt.AddHours(-rules.PostponeDeadlineHours);
            var switchBy = s.StartsAt.AddHours(-rules.ModeSwitchDeadlineHours);
            var open = b.Status == BookingStatus.Booked && s.IsChangeable;
            var other = b.Mode == SeatMode.Online ? SeatMode.Offline : SeatMode.Online;
            var otherFree = s.DeliveryMode == "Hybrid" && r.Seat is not null ? r.Seat.Free(other) : 0;
            return new MyBookingView(b.Id, s.Id, s.ClassId, codes.GetValueOrDefault(s.ClassId, string.Empty), b.StudentId,
                names.GetValueOrDefault(b.StudentId, string.Empty), s.SequenceNo, s.StartsAt, s.EndsAt, s.DeliveryMode, s.Status.ToString(),
                b.Mode.ToString(), b.Kind.ToString(), b.Status.ToString(), postponeBy, switchBy,
                CanPostpone: open && b.Kind == BookingKind.Regular && now <= postponeBy,
                CanCancel: open && (b.Kind is BookingKind.Makeup or BookingKind.Flexible) && now <= postponeBy,
                CanSwitchMode: open && s.DeliveryMode == "Hybrid" && now <= switchBy && otherFree > 0,
                otherFree);
        }).ToList();
    }

    public async Task<IReadOnlyList<MyEntitlementView>> ListMyEntitlementsAsync(Guid userId, CancellationToken ct)
    {
        var students = await crm.ListStudentsOfUserAsync(userId, ct);
        var ids = students.Select(s => s.Id).ToArray();
        var now = clock.GetUtcNow();
        var since = now.AddDays(-90);
        var rows = await (from e in db.Entitlements.AsNoTracking()
                          join s in db.Sessions.AsNoTracking() on e.SourceSessionId equals s.Id
                          where ids.Contains(e.StudentId) && (e.ExpiresAt > since || e.UsedByBookingId != null)
                          orderby e.CreatedAt descending
                          select new { Entitlement = e, Source = s }).Take(100).ToListAsync(ct);
        var usedIds = rows.Where(r => r.Entitlement.UsedByBookingId != null).Select(r => r.Entitlement.UsedByBookingId!.Value).ToArray();
        var uses = await (from b in db.SessionBookings.AsNoTracking()
                          join s in db.Sessions.AsNoTracking() on b.SessionId equals s.Id
                          where usedIds.Contains(b.Id)
                          select new { b.Id, b.SessionId, s.ClassId, s.StartsAt, b.Mode }).ToListAsync(ct);
        var codes = await ClassCodesAsync(rows.Select(r => r.Entitlement.ClassId).Concat(uses.Select(u => u.ClassId)), ct);
        var names = students.ToDictionary(s => s.Id, s => s.FullName);
        return rows.Select(r =>
        {
            var e = r.Entitlement;
            var use = uses.FirstOrDefault(u => u.Id == e.UsedByBookingId);
            var state = e.UsedByBookingId is not null ? "Used" : e.DeclinedAt is not null ? "Declined" : e.ExpiresAt <= now ? "Expired" : "Open";
            return new MyEntitlementView(e.Id, e.StudentId, names.GetValueOrDefault(e.StudentId, string.Empty), e.ClassId,
                codes.GetValueOrDefault(e.ClassId, string.Empty), r.Source.SequenceNo, r.Source.StartsAt, e.ExpiresAt, state, e.RequestedAt,
                use is null ? null : new MakeupUseView(use.Id, use.SessionId, codes.GetValueOrDefault(use.ClassId, string.Empty), use.StartsAt, use.Mode.ToString()));
        }).ToList();
    }

    /// <summary>Sessions of the same lesson in other classes, before the right expires (full ones too, to join their waitlist).</summary>
    public async Task<IReadOnlyList<MakeupOptionView>?> MakeupOptionsAsync(Guid userId, Guid entitlementId, CancellationToken ct)
    {
        var students = (await crm.ListStudentsOfUserAsync(userId, ct)).Select(s => s.Id).ToHashSet();
        var entitlement = await db.Entitlements.AsNoTracking().FirstOrDefaultAsync(e => e.Id == entitlementId, ct);
        if (entitlement is null || !students.Contains(entitlement.StudentId))
        {
            return null;
        }

        var now = clock.GetUtcNow();
        if (!entitlement.IsOpen(now) || entitlement.LessonId is not { } lessonId)
        {
            return [];
        }

        var sessions = await db.Sessions
            .Where(s => s.LessonId == lessonId && s.ClassId != entitlement.ClassId && s.StartsAt > now && s.StartsAt <= entitlement.ExpiresAt
                        && (s.Status == SessionStatus.Scheduled || s.Status == SessionStatus.Provisioned))
            .OrderBy(s => s.StartsAt).Take(50).ToListAsync(ct);
        return await OptionsAsync(sessions, entitlement.StudentId, lessonId, ct);
    }

    public async Task<IReadOnlyList<MyWaitlistView>> ListMyWaitlistAsync(Guid userId, CancellationToken ct)
    {
        var ids = (await crm.ListStudentsOfUserAsync(userId, ct)).Select(s => s.Id).ToArray();
        var rows = await (from w in db.Waitlist.AsNoTracking()
                          join s in db.Sessions.AsNoTracking() on w.SessionId equals s.Id
                          where ids.Contains(w.StudentId) && (w.Status == WaitlistStatus.Waiting || w.Status == WaitlistStatus.Offered)
                          orderby s.StartsAt
                          select new { Entry = w, Session = s }).ToListAsync(ct);
        var codes = await ClassCodesAsync(rows.Select(r => r.Session.ClassId), ct);
        return rows.Select(r => new MyWaitlistView(r.Entry.Id, r.Session.Id, codes.GetValueOrDefault(r.Session.ClassId, string.Empty),
            r.Session.SequenceNo, r.Session.StartsAt, r.Entry.Kind.ToString(), r.Entry.Mode.ToString(), r.Entry.Status.ToString(),
            r.Entry.OfferExpiresAt)).ToList();
    }

    /// <summary>The learner's flexible classes and their sessions of the next weeks, with free seats and own bookings.</summary>
    public async Task<FlexibleView> FlexibleAsync(Guid userId, CancellationToken ct)
    {
        var students = await crm.ListStudentsOfUserAsync(userId, ct);
        if (students.Count == 0)
        {
            return new FlexibleView([], []);
        }

        var enrollments = await academic.ListEnrollmentsOfStudentsAsync(students.Select(s => s.Id).ToList(), ct);
        var classes = new List<(EnrollmentDto Enrollment, ClassDto Class)>();
        foreach (var enrollment in enrollments.Where(e => e.Status == "Active"))
        {
            if (await academic.GetClassAsync(enrollment.ClassId, ct) is { BookingMode: "Flexible" } cls)
            {
                classes.Add((enrollment, cls));
            }
        }

        if (classes.Count == 0)
        {
            return new FlexibleView([], []);
        }

        var now = clock.GetUtcNow();
        var until = now.AddDays(FlexibleDaysAhead);
        var classIds = classes.Select(c => c.Class.Id).ToArray();
        var studentIds = classes.Select(c => c.Enrollment.StudentId).ToArray();
        var sessions = await db.Sessions
            .Where(s => classIds.Contains(s.ClassId) && s.StartsAt > now && s.StartsAt < until
                        && (s.Status == SessionStatus.Scheduled || s.Status == SessionStatus.Provisioned))
            .OrderBy(s => s.StartsAt).ToListAsync(ct);
        var seatRows = new Dictionary<Guid, SessionSeat>();
        foreach (var session in sessions)
        {
            seatRows[session.Id] = await seats.EnsureAsync(session, ct, classes.First(c => c.Class.Id == session.ClassId).Class);
        }

        var sessionIds = sessions.Select(s => s.Id).ToArray();
        var bookings = await db.SessionBookings.AsNoTracking()
            .Where(b => sessionIds.Contains(b.SessionId) && studentIds.Contains(b.StudentId) && b.Status != BookingStatus.Cancelled)
            .ToListAsync(ct);
        var waiting = await db.Waitlist.AsNoTracking()
            .Where(w => sessionIds.Contains(w.SessionId) && studentIds.Contains(w.StudentId)
                        && (w.Status == WaitlistStatus.Waiting || w.Status == WaitlistStatus.Offered))
            .Select(w => w.SessionId).ToListAsync(ct);
        var accounts = await billing.ListCreditAccountsAsync(studentIds, ct);
        var names = students.ToDictionary(s => s.Id, s => s.FullName);

        var classViews = classes.Select(c =>
        {
            var account = accounts.FirstOrDefault(a => a.EnrollmentId == c.Enrollment.Id);
            var ahead = bookings.Count(b => b.EnrollmentId == c.Enrollment.Id && b.Status == BookingStatus.Booked);
            return new FlexibleClassView(c.Class.Id, c.Class.Code, c.Enrollment.StudentId, names.GetValueOrDefault(c.Enrollment.StudentId, string.Empty),
                c.Enrollment.Id, account?.Total, ahead);
        }).ToList();
        var sessionViews = sessions.Select(s =>
        {
            var mine = bookings.FirstOrDefault(b => b.SessionId == s.Id);
            var seat = seatRows[s.Id];
            return new FlexibleSessionView(s.Id, s.ClassId, s.SequenceNo, s.StartsAt, s.EndsAt, s.DeliveryMode, seat.Free(SeatMode.Online),
                seat.Free(SeatMode.Offline), mine?.Id, mine?.Status.ToString(), mine?.Mode.ToString(), waiting.Contains(s.Id));
        }).ToList();
        return new FlexibleView(classViews, sessionViews);
    }

    /// <summary>Learners asking for an exceptional make-up, in the branches the staff member schedules.</summary>
    public async Task<IReadOnlyList<MakeupRequestView>> ListMakeupRequestsAsync(CancellationToken ct)
    {
        var rows = await (from e in db.Entitlements.AsNoTracking()
                          join s in db.Sessions.AsNoTracking() on e.SourceSessionId equals s.Id
                          where e.RequestedAt != null && e.UsedByBookingId == null && e.DeclinedAt == null
                          orderby e.RequestedAt
                          select new { Entitlement = e, Source = s }).Take(200).ToListAsync(ct);
        var views = new List<MakeupRequestView>();
        foreach (var row in rows)
        {
            var e = row.Entitlement;
            if (await academic.GetClassAsync(e.ClassId, ct) is not { } cls || !await access.CanAsync(Permissions.Schedules, cls.BranchId, ct))
            {
                continue;
            }

            var student = await crm.GetStudentAsync(e.StudentId, ct);
            views.Add(new MakeupRequestView(e.Id, e.StudentId, student?.FullName ?? string.Empty, e.ClassId, cls.Code, cls.BranchId,
                row.Source.SequenceNo, row.Source.StartsAt, e.ExpiresAt, e.RequestedAt!.Value, e.RequestNote));
        }

        return views;
    }

    /// <summary>Where staff may place a make-up by hand: coming sessions of the same course level (same lesson first).</summary>
    public async Task<IReadOnlyList<MakeupOptionView>?> StaffOptionsAsync(Guid entitlementId, CancellationToken ct)
    {
        var entitlement = await db.Entitlements.AsNoTracking().FirstOrDefaultAsync(e => e.Id == entitlementId, ct);
        if (entitlement is null || await academic.GetClassAsync(entitlement.ClassId, ct) is not { } cls
                                || !await access.CanAsync(Permissions.Schedules, cls.BranchId, ct))
        {
            return null;
        }

        var lessonIds = (await academic.GetLessonsAsync(entitlement.ClassId, ct)).Select(l => (Guid?)l.Id).ToArray();
        var now = clock.GetUtcNow();
        var sessions = await db.Sessions
            .Where(s => lessonIds.Contains(s.LessonId) && s.ClassId != entitlement.ClassId && s.StartsAt > now
                        && (s.Status == SessionStatus.Scheduled || s.Status == SessionStatus.Provisioned))
            .OrderBy(s => s.StartsAt).Take(100).ToListAsync(ct);
        var options = await OptionsAsync(sessions, entitlement.StudentId, entitlement.LessonId, ct);
        return options.OrderByDescending(o => o.SameLesson).ThenBy(o => o.StartsAt).ToList();
    }

    /// <summary>Learners who asked for leave at least <paramref name="minimum"/> times in the last <paramref name="days"/> days.</summary>
    public async Task<IReadOnlyList<FrequentPostponeView>> FrequentPostponesAsync(int days, int minimum, CancellationToken ct)
    {
        var since = clock.GetUtcNow().AddDays(-Math.Clamp(days, 1, 365));
        var groups = await db.SessionBookings.AsNoTracking()
            .Where(b => b.Status == BookingStatus.Postponed && b.PostponedAt >= since)
            .GroupBy(b => new { b.StudentId, b.EnrollmentId, b.ClassId })
            .Select(g => new { g.Key.StudentId, g.Key.EnrollmentId, g.Key.ClassId, Count = g.Count(), Last = g.Max(b => b.PostponedAt) })
            .Where(g => g.Count >= Math.Max(1, minimum))
            .OrderByDescending(g => g.Count).Take(200).ToListAsync(ct);
        var views = new List<FrequentPostponeView>();
        foreach (var g in groups)
        {
            if (await academic.GetClassAsync(g.ClassId, ct) is not { } cls || !await access.CanAsync(Permissions.Schedules, cls.BranchId, ct))
            {
                continue;
            }

            var student = await crm.GetStudentAsync(g.StudentId, ct);
            views.Add(new FrequentPostponeView(g.StudentId, student?.FullName ?? string.Empty, g.EnrollmentId, g.ClassId, cls.Code, g.Count, g.Last!.Value));
        }

        return views;
    }

    private async Task<IReadOnlyList<MakeupOptionView>> OptionsAsync(IReadOnlyList<Session> sessions, Guid studentId, Guid? lessonId, CancellationToken ct)
    {
        var sessionIds = sessions.Select(s => s.Id).ToArray();
        var taken = (await db.SessionBookings.AsNoTracking()
                .Where(b => sessionIds.Contains(b.SessionId) && b.StudentId == studentId && b.Status != BookingStatus.Cancelled)
                .Select(b => b.SessionId).ToListAsync(ct)).ToHashSet();
        var codes = await ClassCodesAsync(sessions.Select(s => s.ClassId), ct);
        var options = new List<MakeupOptionView>();
        foreach (var session in sessions.Where(s => !taken.Contains(s.Id)))
        {
            var seat = await seats.EnsureAsync(session, ct);
            options.Add(new MakeupOptionView(session.Id, session.ClassId, codes.GetValueOrDefault(session.ClassId, string.Empty), session.SequenceNo,
                session.StartsAt, session.EndsAt, session.DeliveryMode, seat.Free(SeatMode.Online), seat.Free(SeatMode.Offline), seat.FreeMakeup,
                session.LessonId == lessonId));
        }

        return options;
    }

    private async Task<Dictionary<Guid, string>> ClassCodesAsync(IEnumerable<Guid> classIds, CancellationToken ct)
    {
        var codes = new Dictionary<Guid, string>();
        foreach (var id in classIds.Distinct())
        {
            if (await academic.GetClassAsync(id, ct) is { } cls)
            {
                codes[id] = cls.Code;
            }
        }

        return codes;
    }

    /// <summary>
    /// Classes scheduled before bookings existed have sessions without seats: their Regular bookings are made on first read.
    /// </summary>
    private async Task BackfillAsync(IReadOnlyCollection<Guid> studentIds, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var classIds = (await academic.ListEnrollmentsOfStudentsAsync(studentIds, ct)).Select(e => e.ClassId).Distinct().ToArray();
        var now = clock.GetUtcNow();
        var missing = await db.Sessions
            .Where(s => classIds.Contains(s.ClassId) && s.StartsAt > now && s.StartsAt >= from && s.StartsAt < to && s.Status != SessionStatus.Cancelled
                        && !db.Seats.Any(seat => seat.SessionId == s.Id))
            .Select(s => s.ClassId).Distinct().ToListAsync(ct);
        foreach (var classId in missing)
        {
            await sync.SyncClassAsync(classId, ct);
        }
    }
}
