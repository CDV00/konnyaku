using Konnyaku.SharedKernel;

namespace Konnyaku.Learning.Domain;

public enum AttendanceStatus
{
    Present,
    Late,
    LeftEarly,
    Absent,
    ExcusedAbsent,
    Makeup,
}

public enum AttendanceSource
{
    Auto,
    Manual,
    Override,
}

/// <summary>
/// Presence of one enrolled student in one session. Drafted from the meeting's participant log, confirmed by the
/// teacher; only confirmation spends a lesson (event to Billing).
/// </summary>
public sealed class Attendance : Entity, ITenantOwned
{
    private Attendance()
    {
    }

    public Attendance(Guid sessionId, Guid classId, Guid branchId, DateOnly localDate, Guid enrollmentId, Guid studentId,
        AttendanceStatus status, AttendanceSource source, DateTimeOffset? joinedAt, DateTimeOffset? leftAt, int presentSeconds)
    {
        SessionId = sessionId;
        ClassId = classId;
        BranchId = branchId;
        LocalDate = localDate;
        EnrollmentId = enrollmentId;
        StudentId = studentId;
        Status = status;
        Source = source;
        JoinedAt = joinedAt;
        LeftAt = leftAt;
        PresentSeconds = presentSeconds;
    }

    public Guid TenantId { get; private set; }
    public Guid SessionId { get; private set; }
    public Guid ClassId { get; private set; }
    public Guid BranchId { get; private set; }
    public DateOnly LocalDate { get; private set; }
    public Guid EnrollmentId { get; private set; }
    public Guid StudentId { get; private set; }
    public AttendanceStatus Status { get; private set; }
    public AttendanceSource Source { get; private set; }
    public DateTimeOffset? JoinedAt { get; private set; }
    public DateTimeOffset? LeftAt { get; private set; }
    public int PresentSeconds { get; private set; }
    public bool Billable { get; private set; }
    public Guid? ConfirmedBy { get; private set; }
    public DateTimeOffset? ConfirmedAt { get; private set; }

    public bool IsConfirmed => ConfirmedAt is not null;

    public void Override(AttendanceStatus status)
    {
        if (status != Status)
        {
            Status = status;
            Source = AttendanceSource.Override;
        }
    }

    public void Confirm(Guid confirmedBy, DateTimeOffset now)
    {
        ConfirmedBy = confirmedBy;
        ConfirmedAt = now;
        Billable = AttendancePolicy.IsBillable(Status);
    }
}

/// <summary>
/// Default rules (tenant-configurable later): present ≥ 70% of the lesson, late after 10 minutes, under 30% is absent.
/// Unexcused absence still spends the lesson; excused absence does not.
/// </summary>
public static class AttendancePolicy
{
    public const double PresentRatio = 0.7;
    public const double MinimumRatio = 0.3;
    public static readonly TimeSpan LateAfter = TimeSpan.FromMinutes(10);

    public static AttendanceStatus Classify(DateTimeOffset sessionStart, DateTimeOffset sessionEnd, DateTimeOffset? firstJoin, int presentSeconds)
    {
        var lessonSeconds = (sessionEnd - sessionStart).TotalSeconds;
        if (firstJoin is null || lessonSeconds <= 0 || presentSeconds < lessonSeconds * MinimumRatio)
        {
            return AttendanceStatus.Absent;
        }

        if (firstJoin.Value - sessionStart > LateAfter)
        {
            return AttendanceStatus.Late;
        }

        return presentSeconds >= lessonSeconds * PresentRatio ? AttendanceStatus.Present : AttendanceStatus.LeftEarly;
    }

    public static bool IsBillable(AttendanceStatus status) => status is not (AttendanceStatus.ExcusedAbsent);
}

public enum RecordingAccessPolicy
{
    AttendeesOnly,
    AllEnrolled,
}

/// <summary>A learner allowed to watch a session recording (the source of the learner's "replay" list).</summary>
public sealed class RecordingAccess : Entity, ITenantOwned
{
    private RecordingAccess()
    {
    }

    public RecordingAccess(Guid sessionId, Guid recordingId, Guid userId)
    {
        SessionId = sessionId;
        RecordingId = recordingId;
        UserId = userId;
    }

    public Guid TenantId { get; private set; }
    public Guid SessionId { get; private set; }
    public Guid RecordingId { get; private set; }
    public Guid UserId { get; private set; }
    public bool Published { get; private set; }

    public void MarkPublished() => Published = true;
}
