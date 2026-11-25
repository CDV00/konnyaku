using Konnyaku.SharedKernel;

namespace Konnyaku.Scheduling.Domain;

/// <summary>Where a learner sits in a session: in the meeting room (Online) or in the classroom (Offline).</summary>
public enum SeatMode
{
    Online,
    Offline,
}

public enum BookingKind
{
    /// <summary>The learner's own class (made from the enrollment).</summary>
    Regular,

    /// <summary>Replaces a postponed session, in another class teaching the same lesson.</summary>
    Makeup,

    /// <summary>A session of a flexible class, booked one by one.</summary>
    Flexible,

    Trial,
}

public enum BookingStatus
{
    Booked,

    /// <summary>Learner asked for leave before the deadline: no attendance, no lesson spent, a make-up right instead.</summary>
    Postponed,
    Cancelled,
    Attended,
    NoShow,
}

/// <summary>
/// Seats of one session and how many are taken. Counters change only through conditional UPDATEs
/// (<c>... WHERE offline_booked &lt; offline_capacity</c>), so two learners can never take the last seat together.
/// Offline seats never exceed the room capacity. Make-up learners use the normal seats and, in addition, one of
/// <see cref="MakeupCapacity"/>. Waitlist offers held for a learner count as taken.
/// </summary>
public sealed class SessionSeat : Entity, ITenantOwned
{
    private SessionSeat()
    {
    }

    public SessionSeat(Guid sessionId, int onlineCapacity, int offlineCapacity, int makeupCapacity)
    {
        SessionId = sessionId;
        OnlineCapacity = onlineCapacity;
        OfflineCapacity = offlineCapacity;
        MakeupCapacity = makeupCapacity;
    }

    public Guid TenantId { get; private set; }
    public Guid SessionId { get; private set; }
    public int OnlineCapacity { get; private set; }
    public int OfflineCapacity { get; private set; }
    public int OnlineBooked { get; private set; }
    public int OfflineBooked { get; private set; }
    public int MakeupCapacity { get; private set; }
    public int MakeupBooked { get; private set; }

    public int Capacity(SeatMode mode) => mode == SeatMode.Online ? OnlineCapacity : OfflineCapacity;

    public int Booked(SeatMode mode) => mode == SeatMode.Online ? OnlineBooked : OfflineBooked;

    public int Free(SeatMode mode) => Math.Max(0, Capacity(mode) - Booked(mode));

    public int FreeMakeup => Math.Max(0, MakeupCapacity - MakeupBooked);

    /// <summary>New capacities (class size, room or delivery mode changed). Seats already taken are kept even when over.</summary>
    public void Resize(int onlineCapacity, int offlineCapacity, int makeupCapacity)
    {
        OnlineCapacity = onlineCapacity;
        OfflineCapacity = offlineCapacity;
        MakeupCapacity = makeupCapacity;
    }

    /// <summary>Capacities for a session: the class size plus make-up seats, offline capped by the room.</summary>
    public static (int Online, int Offline) CapacityFor(string deliveryMode, int classCapacity, int makeupSeats, int? roomCapacity)
    {
        var total = classCapacity + makeupSeats;
        var offline = roomCapacity is { } room ? Math.Min(total, room) : total;
        return deliveryMode switch
        {
            "Online" => (total, 0),
            "Offline" => (0, offline),
            _ => (total, offline),
        };
    }
}

/// <summary>A learner's seat in one session. The session roster is the list of these, not the class enrollments.</summary>
public sealed class SessionBooking : Entity, ITenantOwned
{
    private SessionBooking()
    {
    }

    public SessionBooking(Guid sessionId, Guid classId, Guid studentId, Guid enrollmentId, SeatMode mode, BookingKind kind,
        Guid? sourceBookingId, DateTimeOffset now)
    {
        SessionId = sessionId;
        ClassId = classId;
        StudentId = studentId;
        EnrollmentId = enrollmentId;
        Mode = mode;
        Kind = kind;
        Status = BookingStatus.Booked;
        SourceBookingId = sourceBookingId;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public Guid TenantId { get; private set; }
    public Guid SessionId { get; private set; }

    /// <summary>Class of the session (for a make-up: the host class, not the learner's own).</summary>
    public Guid ClassId { get; private set; }
    public Guid StudentId { get; private set; }

    /// <summary>The enrollment (and its lesson package) the attendance is charged to: the learner's own class for a make-up.</summary>
    public Guid EnrollmentId { get; private set; }
    public SeatMode Mode { get; private set; }
    public BookingKind Kind { get; private set; }
    public BookingStatus Status { get; private set; }

    /// <summary>For a make-up: the postponed booking it replaces.</summary>
    public Guid? SourceBookingId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? PostponedAt { get; private set; }

    /// <summary>Staff member who placed a make-up by hand (exception), if any.</summary>
    public Guid? PlacedBy { get; private set; }
    public uint Version { get; private set; }

    public bool HoldsSeat => Status == BookingStatus.Booked;

    public Result<SessionBooking> Postpone(DateTimeOffset now)
    {
        if (Kind != BookingKind.Regular || Status != BookingStatus.Booked)
        {
            return Error.Conflict("scheduling.booking_not_postponable");
        }

        Status = BookingStatus.Postponed;
        PostponedAt = now;
        UpdatedAt = now;
        return this;
    }

    public void Cancel(DateTimeOffset now)
    {
        Status = BookingStatus.Cancelled;
        UpdatedAt = now;
    }

    /// <summary>Books again a row released earlier (e.g. by a freeze, or a learner booking the same session again).</summary>
    public void Rebook(Guid enrollmentId, SeatMode mode, BookingKind kind, Guid? sourceBookingId, DateTimeOffset now)
    {
        EnrollmentId = enrollmentId;
        Mode = mode;
        Kind = kind;
        SourceBookingId = sourceBookingId;
        Status = BookingStatus.Booked;
        PostponedAt = null;
        PlacedBy = null;
        UpdatedAt = now;
    }

    public void SwitchMode(SeatMode mode, DateTimeOffset now)
    {
        Mode = mode;
        UpdatedAt = now;
    }

    public void PlacedByStaff(Guid userId) => PlacedBy = userId;

    /// <summary>Teacher confirmed attendance: the booking records the outcome.</summary>
    public void RecordAttendance(bool present, DateTimeOffset now)
    {
        if (Status is BookingStatus.Booked or BookingStatus.Attended or BookingStatus.NoShow)
        {
            Status = present ? BookingStatus.Attended : BookingStatus.NoShow;
            UpdatedAt = now;
        }
    }
}

/// <summary>
/// The right to attend one make-up session, earned by postponing in time. Used once, before <see cref="ExpiresAt"/>,
/// in a session teaching the same lesson; when none fits the learner asks and staff place it by hand.
/// </summary>
public sealed class MakeupEntitlement : Entity, ITenantOwned
{
    private MakeupEntitlement()
    {
    }

    public MakeupEntitlement(Guid studentId, Guid enrollmentId, Guid classId, Guid? lessonId, Guid sourceBookingId, Guid sourceSessionId,
        DateTimeOffset expiresAt, DateTimeOffset now)
    {
        StudentId = studentId;
        EnrollmentId = enrollmentId;
        ClassId = classId;
        LessonId = lessonId;
        SourceBookingId = sourceBookingId;
        SourceSessionId = sourceSessionId;
        ExpiresAt = expiresAt;
        CreatedAt = now;
    }

    public Guid TenantId { get; private set; }
    public Guid StudentId { get; private set; }
    public Guid EnrollmentId { get; private set; }

    /// <summary>The learner's own class.</summary>
    public Guid ClassId { get; private set; }
    public Guid? LessonId { get; private set; }
    public Guid SourceBookingId { get; private set; }
    public Guid SourceSessionId { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid? UsedByBookingId { get; private set; }

    /// <summary>The learner found no session of the same lesson and asked staff to place the make-up by hand.</summary>
    public DateTimeOffset? RequestedAt { get; private set; }
    public string? RequestNote { get; private set; }
    public DateTimeOffset? DeclinedAt { get; private set; }
    public uint Version { get; private set; }

    public bool IsOpen(DateTimeOffset now) => UsedByBookingId is null && DeclinedAt is null && ExpiresAt > now;

    public Result<MakeupEntitlement> EnsureOpen(DateTimeOffset now)
    {
        if (UsedByBookingId is not null)
        {
            return Error.Conflict("scheduling.entitlement_used");
        }

        if (DeclinedAt is not null || ExpiresAt <= now)
        {
            return Error.Conflict("scheduling.entitlement_expired");
        }

        return this;
    }

    public void Use(Guid bookingId) => UsedByBookingId = bookingId;

    /// <summary>The make-up booking was cancelled (by the learner in time, or the session was cancelled): the right returns.</summary>
    public void Release() => UsedByBookingId = null;

    public void Request(string? note, DateTimeOffset now)
    {
        RequestedAt = now;
        RequestNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim()[..Math.Min(note.Trim().Length, 300)];
    }

    public void Decline(DateTimeOffset now) => DeclinedAt = now;
}

public enum WaitlistStatus
{
    Waiting,

    /// <summary>A seat is held for the learner until <see cref="SessionWaitlistEntry.OfferExpiresAt"/>.</summary>
    Offered,
    Accepted,
    Expired,
    Withdrawn,
}

/// <summary>
/// A learner waiting for a seat in a full session (a make-up or a flexible class). When a seat frees up, the Worker offers
/// it to the first in line and holds it for a while; unanswered offers pass to the next.
/// </summary>
public sealed class SessionWaitlistEntry : Entity, ITenantOwned
{
    private SessionWaitlistEntry()
    {
    }

    public SessionWaitlistEntry(Guid sessionId, Guid studentId, Guid enrollmentId, BookingKind kind, SeatMode mode, Guid? entitlementId, DateTimeOffset now)
    {
        SessionId = sessionId;
        StudentId = studentId;
        EnrollmentId = enrollmentId;
        Kind = kind;
        Mode = mode;
        EntitlementId = entitlementId;
        Status = WaitlistStatus.Waiting;
        CreatedAt = now;
    }

    public Guid TenantId { get; private set; }
    public Guid SessionId { get; private set; }
    public Guid StudentId { get; private set; }
    public Guid EnrollmentId { get; private set; }
    public BookingKind Kind { get; private set; }
    public SeatMode Mode { get; private set; }
    public Guid? EntitlementId { get; private set; }
    public WaitlistStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? OfferedAt { get; private set; }
    public DateTimeOffset? OfferExpiresAt { get; private set; }
    public uint Version { get; private set; }

    public bool IsActive => Status is WaitlistStatus.Waiting or WaitlistStatus.Offered;

    public void Offer(DateTimeOffset now, DateTimeOffset expiresAt)
    {
        Status = WaitlistStatus.Offered;
        OfferedAt = now;
        OfferExpiresAt = expiresAt;
    }

    public void Accept() => Status = WaitlistStatus.Accepted;

    public void Expire() => Status = WaitlistStatus.Expired;

    public void Withdraw() => Status = WaitlistStatus.Withdrawn;
}

/// <summary>Booking rules of a center. A center without a row uses <see cref="Defaults"/>.</summary>
public sealed class BookingSettings : Entity, ITenantOwned
{
    private BookingSettings()
    {
    }

    public BookingSettings(int postponeDeadlineHours, int maxPostponesPerEnrollment, int entitlementDays, int modeSwitchDeadlineHours,
        int makeupSeatsPerSession, int waitlistHoldHours)
    {
        Change(postponeDeadlineHours, maxPostponesPerEnrollment, entitlementDays, modeSwitchDeadlineHours, makeupSeatsPerSession, waitlistHoldHours);
    }

    public static BookingSettings Defaults => new(12, 3, 30, 2, 2, 2);

    public Guid TenantId { get; private set; }

    /// <summary>Leave must be asked at least this many hours before the session starts.</summary>
    public int PostponeDeadlineHours { get; private set; }
    public int MaxPostponesPerEnrollment { get; private set; }

    /// <summary>A make-up right is valid for this many days after the leave was asked.</summary>
    public int EntitlementDays { get; private set; }
    public int ModeSwitchDeadlineHours { get; private set; }

    /// <summary>Seats each session keeps for make-up learners, on top of the class size.</summary>
    public int MakeupSeatsPerSession { get; private set; }
    public int WaitlistHoldHours { get; private set; }

    public static bool IsValid(int postponeDeadlineHours, int maxPostponesPerEnrollment, int entitlementDays, int modeSwitchDeadlineHours,
        int makeupSeatsPerSession, int waitlistHoldHours) =>
        postponeDeadlineHours is >= 0 and <= 168 && maxPostponesPerEnrollment is >= 0 and <= 100 && entitlementDays is >= 1 and <= 365
        && modeSwitchDeadlineHours is >= 0 and <= 168 && makeupSeatsPerSession is >= 0 and <= 50 && waitlistHoldHours is >= 1 and <= 72;

    public void Change(int postponeDeadlineHours, int maxPostponesPerEnrollment, int entitlementDays, int modeSwitchDeadlineHours,
        int makeupSeatsPerSession, int waitlistHoldHours)
    {
        PostponeDeadlineHours = postponeDeadlineHours;
        MaxPostponesPerEnrollment = maxPostponesPerEnrollment;
        EntitlementDays = entitlementDays;
        ModeSwitchDeadlineHours = modeSwitchDeadlineHours;
        MakeupSeatsPerSession = makeupSeatsPerSession;
        WaitlistHoldHours = waitlistHoldHours;
    }
}
