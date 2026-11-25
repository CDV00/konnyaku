using System.Text.RegularExpressions;
using Konnyaku.SharedKernel;

namespace Konnyaku.Academic.Domain;

/// <summary>A program teaches one language against one proficiency framework (e.g. Japanese / JLPT).</summary>
public sealed partial class StudyProgram : Entity, ITenantOwned, IAuditable
{
    private StudyProgram()
    {
    }

    public Guid TenantId { get; private set; }
    public string Code { get; private set; } = string.Empty;
    public LocalizedString Name { get; private set; } = null!;
    public string LanguageCode { get; private set; } = string.Empty;
    public string FrameworkCode { get; private set; } = string.Empty;
    public string? AgeGroup { get; private set; }
    public string? Goal { get; private set; }
    public bool IsPublic { get; private set; }

    /// <summary>An inactive program leaves the public catalogue and takes no new levels or classes; existing classes go on.</summary>
    public bool IsActive { get; private set; } = true;

    public List<CourseLevel> Levels { get; private set; } = [];

    Guid IAuditable.AuditSubjectId => Id;

    /// <summary>Language and framework are fixed once levels exist (levels target a level of that framework).</summary>
    public Result<StudyProgram> Update(string code, IReadOnlyDictionary<string, string> name, string languageCode, string frameworkCode,
        string? ageGroup, string? goal, bool isPublic)
    {
        var normalized = NormalizeCode(code);
        if (normalized is null)
        {
            return Error.Validation("academic.code_invalid");
        }

        if (name is null || name.Values.All(string.IsNullOrWhiteSpace))
        {
            return Error.Validation("academic.name_required");
        }

        if ((languageCode != LanguageCode || frameworkCode != FrameworkCode) && Levels.Count > 0)
        {
            return Error.Conflict("academic.program_has_levels");
        }

        Code = normalized;
        Name = new LocalizedString(name);
        LanguageCode = languageCode;
        FrameworkCode = frameworkCode;
        AgeGroup = string.IsNullOrWhiteSpace(ageGroup) ? null : ageGroup.Trim();
        Goal = string.IsNullOrWhiteSpace(goal) ? null : goal.Trim();
        IsPublic = isPublic;
        return this;
    }

    public void SetActive(bool active) => IsActive = active;

    public static Result<StudyProgram> Create(string code, IReadOnlyDictionary<string, string> name, string languageCode,
        string frameworkCode, string? ageGroup, string? goal, bool isPublic)
    {
        var normalized = NormalizeCode(code);
        if (normalized is null)
        {
            return Error.Validation("academic.code_invalid");
        }

        if (name is null || name.Values.All(string.IsNullOrWhiteSpace))
        {
            return Error.Validation("academic.name_required");
        }

        return new StudyProgram
        {
            Code = normalized,
            Name = new LocalizedString(name),
            LanguageCode = languageCode,
            FrameworkCode = frameworkCode,
            AgeGroup = ageGroup,
            Goal = goal,
            IsPublic = isPublic,
            IsActive = true,
        };
    }

    internal static string? NormalizeCode(string? code)
    {
        var normalized = (code ?? string.Empty).Trim().ToUpperInvariant();
        return CodePattern().IsMatch(normalized) ? normalized : null;
    }

    [GeneratedRegex("^[A-Z0-9][A-Z0-9_.-]{1,29}$")]
    private static partial Regex CodePattern();
}

/// <summary>A course level inside a program, targeting a level of the program's framework, with its lessons.</summary>
public sealed class CourseLevel : Entity, ITenantOwned, IAuditable
{
    private CourseLevel()
    {
    }

    public Guid TenantId { get; private set; }
    public Guid ProgramId { get; private set; }
    public string Code { get; private set; } = string.Empty;
    public LocalizedString Name { get; private set; } = null!;
    public string TargetLevelCode { get; private set; } = string.Empty;
    public int Ordinal { get; private set; }
    public int SessionMinutes { get; private set; }

    /// <summary>An inactive level takes no new classes; its classes go on.</summary>
    public bool IsActive { get; private set; } = true;

    public List<CurriculumLesson> Lessons { get; private set; } = [];

    /// <summary>Logged under its program: the program page shows the history of its levels.</summary>
    Guid IAuditable.AuditSubjectId => ProgramId;

    /// <summary>
    /// The number of lessons can change only while no class uses the level (sessions point to lessons). A new length
    /// applies to schedules planned afterwards; planned sessions keep theirs until the class schedule is re-planned.
    /// </summary>
    public Result<CourseLevel> Update(string code, IReadOnlyDictionary<string, string> name, string targetLevelCode, int sessionCount,
        int sessionMinutes, bool usedByClasses)
    {
        var normalized = StudyProgram.NormalizeCode(code);
        if (normalized is null)
        {
            return Error.Validation("academic.code_invalid");
        }

        if (name is null || name.Values.All(string.IsNullOrWhiteSpace))
        {
            return Error.Validation("academic.name_required");
        }

        if (sessionCount is < 1 or > 500 || sessionMinutes is < 15 or > 480)
        {
            return Error.Validation("academic.sessions_invalid");
        }

        if (sessionCount != Lessons.Count && usedByClasses)
        {
            return Error.Conflict("academic.level_in_use");
        }

        Code = normalized;
        Name = new LocalizedString(name);
        TargetLevelCode = targetLevelCode;
        SessionMinutes = sessionMinutes;
        Lessons.RemoveAll(l => l.Ordinal > sessionCount);
        for (var i = Lessons.Count + 1; i <= sessionCount; i++)
        {
            Lessons.Add(CurriculumLesson.Default(Id, i));
        }

        return this;
    }

    public void SetActive(bool active) => IsActive = active;

    public static Result<CourseLevel> Create(Guid programId, string code, IReadOnlyDictionary<string, string> name,
        string targetLevelCode, int ordinal, int sessionCount, int sessionMinutes)
    {
        var normalized = StudyProgram.NormalizeCode(code);
        if (normalized is null)
        {
            return Error.Validation("academic.code_invalid");
        }

        if (sessionCount is < 1 or > 500 || sessionMinutes is < 15 or > 480)
        {
            return Error.Validation("academic.sessions_invalid");
        }

        var level = new CourseLevel
        {
            ProgramId = programId,
            Code = normalized,
            Name = new LocalizedString(name),
            TargetLevelCode = targetLevelCode,
            Ordinal = ordinal,
            SessionMinutes = sessionMinutes,
        };
        for (var i = 1; i <= sessionCount; i++)
        {
            level.Lessons.Add(CurriculumLesson.Default(level.Id, i));
        }

        return level;
    }
}

/// <summary>One lesson of a course level. Sessions point to a lesson so make-ups can find "the same lesson" elsewhere.</summary>
public sealed class CurriculumLesson : Entity, ITenantOwned
{
    private CurriculumLesson()
    {
    }

    internal CurriculumLesson(Guid courseLevelId, int ordinal, LocalizedString title)
    {
        CourseLevelId = courseLevelId;
        Ordinal = ordinal;
        Title = title;
    }

    public Guid TenantId { get; private set; }
    public Guid CourseLevelId { get; private set; }
    public int Ordinal { get; private set; }
    public LocalizedString Title { get; private set; } = null!;

    internal static CurriculumLesson Default(Guid courseLevelId, int ordinal) =>
        new(courseLevelId, ordinal, new LocalizedString(new Dictionary<string, string>
        {
            ["vi"] = $"Buổi {ordinal}",
            ["en"] = $"Lesson {ordinal}",
        }));
}

public enum DeliveryMode
{
    Online,
    Offline,
    Hybrid,
}

public enum ClassStatus
{
    Planned,
    Open,
    Running,
    Completed,
    Cancelled,
}

/// <summary>A class of a course level, owned by a branch (the virtual Online branch for online classes).</summary>
public sealed class Class : Entity, ITenantOwned, IAuditable
{
    private Class()
    {
    }

    public Guid TenantId { get; private set; }
    public string Code { get; private set; } = string.Empty;
    public Guid CourseLevelId { get; private set; }
    public Guid BranchId { get; private set; }
    public DeliveryMode DeliveryMode { get; private set; }
    public int Capacity { get; private set; }
    public int EnrolledCount { get; private set; }
    public DateOnly StartsOn { get; private set; }
    public string TimeZone { get; private set; } = string.Empty;
    public ClassStatus Status { get; private set; }

    public bool IsClosed => Status is ClassStatus.Completed or ClassStatus.Cancelled;

    Guid IAuditable.AuditSubjectId => Id;

    /// <summary>
    /// Code, delivery mode and capacity change any time (capacity never below the seats taken). Level, branch and start
    /// date shape the sessions, so they change only before the class is scheduled; the level also needs an empty class.
    /// </summary>
    public Result<Class> Update(ClassChange change, bool hasSessions)
    {
        if (IsClosed)
        {
            return Error.Conflict("academic.class_closed");
        }

        var normalized = StudyProgram.NormalizeCode(change.Code);
        if (normalized is null)
        {
            return Error.Validation("academic.code_invalid");
        }

        if (change.Capacity is < 1 or > 500)
        {
            return Error.Validation("academic.capacity_invalid");
        }

        if (change.Capacity < EnrolledCount)
        {
            return Error.Conflict("academic.capacity_below_enrolled", new Dictionary<string, object?> { ["enrolled"] = EnrolledCount });
        }

        if (change.CourseLevelId != CourseLevelId && (hasSessions || EnrolledCount > 0))
        {
            return Error.Conflict("academic.class_level_locked");
        }

        if (change.BranchId != BranchId && hasSessions)
        {
            return Error.Conflict("academic.class_branch_locked");
        }

        if (change.StartsOn != StartsOn && hasSessions)
        {
            return Error.Conflict("academic.class_start_locked");
        }

        Code = normalized;
        CourseLevelId = change.CourseLevelId;
        BranchId = change.BranchId;
        TimeZone = change.TimeZone;
        DeliveryMode = change.DeliveryMode;
        Capacity = change.Capacity;
        StartsOn = change.StartsOn;
        return this;
    }

    /// <summary>Completed (taught to the end) or Cancelled (stopped): the remaining planned sessions are cancelled.</summary>
    public Result<Class> Close(ClassStatus status)
    {
        if (status is not (ClassStatus.Completed or ClassStatus.Cancelled))
        {
            return Error.Validation("academic.class_status_invalid");
        }

        if (IsClosed)
        {
            return Error.Conflict("academic.class_closed");
        }

        Status = status;
        return this;
    }

    /// <summary>Reopens a closed class for enrollment. Cancelled sessions are not restored (re-plan the schedule).</summary>
    public Result<Class> Reopen()
    {
        if (!IsClosed)
        {
            return Error.Conflict("academic.class_not_closed");
        }

        Status = ClassStatus.Open;
        return this;
    }

    public static Result<Class> Create(string code, Guid courseLevelId, Guid branchId, DeliveryMode mode, int capacity,
        DateOnly startsOn, string timeZone)
    {
        var normalized = StudyProgram.NormalizeCode(code);
        if (normalized is null)
        {
            return Error.Validation("academic.code_invalid");
        }

        if (capacity is < 1 or > 500)
        {
            return Error.Validation("academic.capacity_invalid");
        }

        return new Class
        {
            Code = normalized,
            CourseLevelId = courseLevelId,
            BranchId = branchId,
            DeliveryMode = mode,
            Capacity = capacity,
            StartsOn = startsOn,
            TimeZone = timeZone,
            Status = ClassStatus.Open,
        };
    }
}

public sealed record ClassChange(string Code, Guid CourseLevelId, Guid BranchId, string TimeZone, DeliveryMode DeliveryMode, int Capacity, DateOnly StartsOn);

public enum EnrollmentType
{
    Regular,
    Trial,
    MakeupOnly,
}

public enum EnrollmentStatus
{
    Pending,
    Active,
    Frozen,
    Completed,
    Rejected,
    Transferred,
    Dropped,
}

public sealed class Enrollment : Entity, ITenantOwned
{
    private Enrollment()
    {
    }

    public Enrollment(Guid classId, Guid studentId, EnrollmentType type, DateOnly startsOn, DateTimeOffset now, Guid? orderId = null)
    {
        OrderId = orderId;
        ClassId = classId;
        StudentId = studentId;
        Type = type;
        Status = EnrollmentStatus.Active;
        StartsOn = startsOn;
        CreatedAt = now;
    }

    public Guid TenantId { get; private set; }
    public Guid ClassId { get; private set; }
    public Guid StudentId { get; private set; }

    /// <summary>The paid order (package) this enrollment consumes lessons from, if any.</summary>
    public Guid? OrderId { get; private set; }

    public EnrollmentType Type { get; private set; }
    public EnrollmentStatus Status { get; private set; }
    public DateOnly StartsOn { get; private set; }
    public DateOnly? FrozenFrom { get; private set; }
    public DateOnly? FrozenTo { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public bool HoldsSeat => Status is EnrollmentStatus.Active or EnrollmentStatus.Frozen;

    public Result<Enrollment> Freeze(DateOnly from, DateOnly to)
    {
        if (Status != EnrollmentStatus.Active)
        {
            return Error.Conflict("academic.enrollment_not_active");
        }

        if (to < from)
        {
            return Error.Validation("academic.freeze_range_invalid");
        }

        Status = EnrollmentStatus.Frozen;
        FrozenFrom = from;
        FrozenTo = to;
        return this;
    }

    public Result<Enrollment> Unfreeze()
    {
        if (Status != EnrollmentStatus.Frozen)
        {
            return Error.Conflict("academic.enrollment_not_frozen");
        }

        Status = EnrollmentStatus.Active;
        return this;
    }

    public Result<Enrollment> Drop()
    {
        if (!HoldsSeat)
        {
            return Error.Conflict("academic.enrollment_closed");
        }

        Status = EnrollmentStatus.Dropped;
        return this;
    }
}
