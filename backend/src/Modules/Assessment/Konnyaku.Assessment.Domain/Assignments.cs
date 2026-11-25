using Konnyaku.SharedKernel;

namespace Konnyaku.Assessment.Domain;

/// <summary>One engine, five uses: they differ only by the settings of the assignment.</summary>
public enum AssignmentKind
{
    Homework,
    Quiz,
    Test,
    MockExam,
    Placement,
}

public enum AssignmentTarget
{
    Class,
    Session,

    /// <summary>Chosen learners of the class.</summary>
    Students,
}

public enum LatePolicy
{
    /// <summary>Late work is accepted and marked late.</summary>
    Accept,

    /// <summary>Accepted with <see cref="Assignment.LatePenaltyPercent"/> off the score.</summary>
    Penalty,

    /// <summary>Nothing after the due time: no new attempt, and open attempts end at the due time.</summary>
    Lock,
}

public enum ScoringRule
{
    Best,
    Last,
    Average,
}

public enum GraderMode
{
    /// <summary>Machine-graded; essays and mismatched short answers still go to the teacher.</summary>
    Auto,

    /// <summary>The teacher reviews and finalizes every attempt.</summary>
    Teacher,

    /// <summary>Classmates grade, the teacher finalizes (later task; not accepted yet).</summary>
    Peer,
}

public enum ScoreRelease
{
    OnSubmit,
    AfterGrading,

    /// <summary>For the whole class at <see cref="Assignment.ReleaseAt"/> (no answers leak while others still work).</summary>
    AtTime,
}

public enum AnswerVisibility
{
    Never,
    AfterSubmit,
    AfterDue,
}

public sealed record AssignmentSettings(
    AssignmentKind Kind, DateTimeOffset OpensAt, DateTimeOffset? DueAt, LatePolicy LatePolicy, int LatePenaltyPercent, int? MaxAttempts,
    ScoringRule ScoringRule, GraderMode Grader, ScoreRelease ScoreRelease, DateTimeOffset? ReleaseAt, AnswerVisibility AnswerVisibility,
    int? TimeLimitMinutes);

/// <summary>A paper given to a class, one of its sessions, or chosen learners of the class.</summary>
public sealed class Assignment : Entity, ITenantOwned
{
    private Assignment()
    {
    }

    public Guid TenantId { get; private set; }
    public Guid PaperId { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public AssignmentTarget Target { get; private set; }
    public Guid ClassId { get; private set; }

    /// <summary>Branch of the class, copied for branch-scoped permissions and lists.</summary>
    public Guid BranchId { get; private set; }

    public Guid? SessionId { get; private set; }
    public List<Guid> StudentIds { get; private set; } = [];
    public AssignmentKind Kind { get; private set; }
    public DateTimeOffset OpensAt { get; private set; }
    public DateTimeOffset? DueAt { get; private set; }
    public LatePolicy LatePolicy { get; private set; }
    public int LatePenaltyPercent { get; private set; }

    /// <summary>Null: unlimited.</summary>
    public int? MaxAttempts { get; private set; }

    public ScoringRule ScoringRule { get; private set; }
    public GraderMode Grader { get; private set; }
    public ScoreRelease ScoreRelease { get; private set; }
    public DateTimeOffset? ReleaseAt { get; private set; }
    public AnswerVisibility AnswerVisibility { get; private set; }

    /// <summary>Overall limit for papers without timed sections.</summary>
    public int? TimeLimitMinutes { get; private set; }

    public Guid CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static Result<Assignment> Create(Guid paperId, string title, AssignmentTarget target, Guid classId, Guid branchId, Guid? sessionId,
        IReadOnlyList<Guid> studentIds, AssignmentSettings settings, Guid createdBy, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return Error.Validation("assessment.title_required");
        }

        if ((target == AssignmentTarget.Session && sessionId is null) || (target == AssignmentTarget.Students && studentIds.Count == 0))
        {
            return Error.Validation("assessment.target_invalid");
        }

        var assignment = new Assignment
        {
            PaperId = paperId,
            Title = title.Trim(),
            Target = target,
            ClassId = classId,
            BranchId = branchId,
            SessionId = target == AssignmentTarget.Session ? sessionId : null,
            StudentIds = target == AssignmentTarget.Students ? studentIds.Distinct().ToList() : [],
            CreatedBy = createdBy,
            CreatedAt = now,
        };
        var applied = assignment.Apply(settings, now);
        return applied.IsSuccess ? assignment : applied.Error!;
    }

    public Result<bool> Update(string title, AssignmentSettings settings, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return Error.Validation("assessment.title_required");
        }

        Title = title.Trim();
        return Apply(settings, now);
    }

    /// <summary>Scores of an <see cref="ScoreRelease.AtTime"/> assignment become visible now.</summary>
    public void ReleaseNow(DateTimeOffset now)
    {
        ScoreRelease = ScoreRelease.AtTime;
        ReleaseAt = now;
        UpdatedAt = now;
    }

    public bool IsFor(Guid studentId) => Target != AssignmentTarget.Students || StudentIds.Contains(studentId);

    public bool IsClosed(DateTimeOffset now) => LatePolicy == LatePolicy.Lock && DueAt is { } due && now >= due;

    private Result<bool> Apply(AssignmentSettings s, DateTimeOffset now)
    {
        if (s.DueAt is { } due && due <= s.OpensAt)
        {
            return Error.Validation("assessment.due_before_open");
        }

        if (s.Grader == GraderMode.Peer)
        {
            return Error.Validation("assessment.peer_grading_unavailable");
        }

        if (s.ScoreRelease == ScoreRelease.AtTime && s.ReleaseAt is null)
        {
            return Error.Validation("assessment.release_time_required");
        }

        if (s.MaxAttempts is <= 0 or > 100 || s.LatePenaltyPercent is < 0 or > 100 || s.TimeLimitMinutes is <= 0 or > 600 ||
            (s.LatePolicy == LatePolicy.Penalty && s.LatePenaltyPercent == 0))
        {
            return Error.Validation("assessment.settings_invalid");
        }

        if (s.AnswerVisibility == AnswerVisibility.AfterDue && s.DueAt is null)
        {
            return Error.Validation("assessment.due_required");
        }

        Kind = s.Kind;
        OpensAt = s.OpensAt;
        DueAt = s.DueAt;
        LatePolicy = s.LatePolicy;
        LatePenaltyPercent = s.LatePolicy == LatePolicy.Penalty ? s.LatePenaltyPercent : 0;
        MaxAttempts = s.MaxAttempts;
        ScoringRule = s.ScoringRule;
        Grader = s.Grader;
        ScoreRelease = s.ScoreRelease;
        ReleaseAt = s.ScoreRelease == ScoreRelease.AtTime ? s.ReleaseAt : null;
        AnswerVisibility = s.AnswerVisibility;
        TimeLimitMinutes = s.TimeLimitMinutes;
        UpdatedAt = now;
        return true;
    }
}

/// <summary>What a learner may see of an attempt, by the assignment's release settings.</summary>
public static class ReleasePolicy
{
    /// <summary>Score and per-item correctness (a not yet finished grading shows as pending).</summary>
    public static bool ScoresVisible(Assignment assignment, Attempt attempt, DateTimeOffset now) =>
        attempt.Status != AttemptStatus.InProgress && assignment.ScoreRelease switch
        {
            ScoreRelease.OnSubmit => true,
            ScoreRelease.AfterGrading => attempt.Status == AttemptStatus.Graded,
            ScoreRelease.AtTime => assignment.ReleaseAt is { } at && now >= at,
            _ => false,
        };

    /// <summary>Correct answers and explanations: never before the scores.</summary>
    public static bool AnswersVisible(Assignment assignment, Attempt attempt, DateTimeOffset now) =>
        ScoresVisible(assignment, attempt, now) && assignment.AnswerVisibility switch
        {
            AnswerVisibility.AfterSubmit => true,
            AnswerVisibility.AfterDue => assignment.DueAt is { } due && now >= due,
            _ => false,
        };

    /// <summary>When the learner sees an attempt graded at <paramref name="gradedAt"/> (for notifications).</summary>
    public static DateTimeOffset VisibleFrom(Assignment assignment, DateTimeOffset gradedAt) =>
        assignment.ScoreRelease == ScoreRelease.AtTime && assignment.ReleaseAt is { } at && at > gradedAt ? at : gradedAt;

    /// <summary>The assignment score of a learner from their graded attempts (oldest first).</summary>
    public static decimal? FinalScore(ScoringRule rule, IReadOnlyList<decimal> graded) =>
        graded.Count == 0 ? null : rule switch
        {
            ScoringRule.Last => graded[^1],
            ScoringRule.Average => Math.Round(graded.Average(), 2),
            _ => graded.Max(),
        };
}
