using Konnyaku.SharedKernel;

namespace Konnyaku.Scheduling.Domain;

/// <summary>Weekly rule of a class (days + local start time in the class time zone).</summary>
public sealed class SchedulePattern : Entity, ITenantOwned
{
    private SchedulePattern()
    {
    }

    public SchedulePattern(Guid classId, IReadOnlyCollection<int> isoDays, TimeOnly startTime, int durationMinutes,
        string timeZone, DateOnly effectiveFrom, Guid? teacherId, Guid? roomId, DateTimeOffset now)
    {
        ClassId = classId;
        IsoDays = isoDays.Distinct().Order().ToList();
        StartTime = startTime;
        DurationMinutes = durationMinutes;
        TimeZone = timeZone;
        EffectiveFrom = effectiveFrom;
        TeacherId = teacherId;
        RoomId = roomId;
        CreatedAt = now;
    }

    public Guid TenantId { get; private set; }
    public Guid ClassId { get; private set; }

    /// <summary>ISO day numbers: 1 = Monday … 7 = Sunday.</summary>
    public List<int> IsoDays { get; private set; } = [];

    public TimeOnly StartTime { get; private set; }
    public int DurationMinutes { get; private set; }
    public string TimeZone { get; private set; } = string.Empty;
    public DateOnly EffectiveFrom { get; private set; }
    public Guid? TeacherId { get; private set; }
    public Guid? RoomId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
}

public enum SessionStatus
{
    Scheduled,
    Provisioned,
    InProgress,
    Completed,
    Cancelled,
}

/// <summary>One concrete lesson of a class. Times are stored in UTC; <see cref="LocalDate"/> is in the class time zone.</summary>
public sealed class Session : Entity, ITenantOwned
{
    private Session()
    {
    }

    public Session(Guid classId, Guid branchId, Guid? patternId, Guid? lessonId, int sequenceNo, PlannedSlot slot,
        string timeZone, string deliveryMode, Guid? teacherId, Guid? roomId)
    {
        ClassId = classId;
        BranchId = branchId;
        PatternId = patternId;
        LessonId = lessonId;
        SequenceNo = sequenceNo;
        StartsAt = slot.StartsAt;
        EndsAt = slot.EndsAt;
        LocalDate = slot.LocalDate;
        TimeZone = timeZone;
        DeliveryMode = deliveryMode;
        PlannedTeacherId = teacherId;
        ActualTeacherId = teacherId;
        RoomId = roomId;
        Status = SessionStatus.Scheduled;
    }

    public Guid TenantId { get; private set; }
    public Guid ClassId { get; private set; }
    public Guid BranchId { get; private set; }
    public Guid? PatternId { get; private set; }
    public Guid? LessonId { get; private set; }
    public int SequenceNo { get; private set; }
    public DateTimeOffset StartsAt { get; private set; }
    public DateTimeOffset EndsAt { get; private set; }
    public DateOnly LocalDate { get; private set; }
    public string TimeZone { get; private set; } = string.Empty;
    public string DeliveryMode { get; private set; } = string.Empty;
    public Guid? PlannedTeacherId { get; private set; }
    public Guid? ActualTeacherId { get; private set; }
    public Guid? RoomId { get; private set; }
    public SessionStatus Status { get; private set; }
    public string? CancelReason { get; private set; }
    public uint Version { get; private set; }

    public bool IsChangeable => Status is SessionStatus.Scheduled or SessionStatus.Provisioned;

    public Result<Session> MoveTo(PlannedSlot slot)
    {
        if (!IsChangeable)
        {
            return Error.Conflict("scheduling.session_not_changeable");
        }

        StartsAt = slot.StartsAt;
        EndsAt = slot.EndsAt;
        LocalDate = slot.LocalDate;
        return this;
    }

    public Result<Session> AssignTeacher(Guid teacherId)
    {
        if (!IsChangeable)
        {
            return Error.Conflict("scheduling.session_not_changeable");
        }

        PlannedTeacherId ??= teacherId;
        ActualTeacherId = teacherId;
        return this;
    }

    public Result<Session> Cancel(string reason)
    {
        if (!IsChangeable)
        {
            return Error.Conflict("scheduling.session_not_changeable");
        }

        Status = SessionStatus.Cancelled;
        CancelReason = reason;
        return this;
    }
}

public static class ResourceTypes
{
    public const string Teacher = "teacher";
    public const string Room = "room";
}

/// <summary>
/// A teacher or room held by a session. PostgreSQL rejects two active bookings of the same resource that overlap
/// (exclusion constraint on a generated tstzrange) — the database is the final judge, even under concurrency.
/// </summary>
public sealed class ResourceBooking : Entity, ITenantOwned
{
    private ResourceBooking()
    {
    }

    public ResourceBooking(Guid sessionId, string resourceType, Guid resourceId, DateTimeOffset startsAt, DateTimeOffset endsAt)
    {
        SessionId = sessionId;
        ResourceType = resourceType;
        ResourceId = resourceId;
        StartsAt = startsAt;
        EndsAt = endsAt;
        IsActive = true;
    }

    public Guid TenantId { get; private set; }
    public Guid SessionId { get; private set; }
    public string ResourceType { get; private set; } = string.Empty;
    public Guid ResourceId { get; private set; }
    public DateTimeOffset StartsAt { get; private set; }
    public DateTimeOffset EndsAt { get; private set; }
    public bool IsActive { get; private set; }

    public void Release() => IsActive = false;

    public void MoveTo(DateTimeOffset startsAt, DateTimeOffset endsAt)
    {
        StartsAt = startsAt;
        EndsAt = endsAt;
    }
}

public sealed record PlannedSlot(DateOnly LocalDate, DateTimeOffset StartsAt, DateTimeOffset EndsAt);

/// <summary>Turns local wall-clock times of a time zone into UTC instants, handling daylight saving changes.</summary>
public static class SchedulePlanner
{
    public const int MaxDaysAhead = 3 * 366;

    public static PlannedSlot Slot(DateOnly date, TimeOnly start, int durationMinutes, string timeZone)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZone);
        var local = date.ToDateTime(start, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            // Wall-clock time skipped by a DST jump (e.g. 02:30): start right after the jump.
            local = local.AddHours(1);
        }

        var startsAt = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
        return new PlannedSlot(date, startsAt, startsAt.AddMinutes(durationMinutes));
    }

    /// <summary>The next <paramref name="count"/> dates from <paramref name="from"/> on the given ISO weekdays, skipping days off.</summary>
    public static IReadOnlyList<PlannedSlot> Plan(DateOnly from, IReadOnlyCollection<int> isoDays, TimeOnly start,
        int durationMinutes, string timeZone, int count, IReadOnlySet<DateOnly> daysOff)
    {
        var slots = new List<PlannedSlot>(count);
        for (var date = from; slots.Count < count && date <= from.AddDays(MaxDaysAhead); date = date.AddDays(1))
        {
            var iso = date.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)date.DayOfWeek;
            if (isoDays.Contains(iso) && !daysOff.Contains(date))
            {
                slots.Add(Slot(date, start, durationMinutes, timeZone));
            }
        }

        return slots;
    }
}
