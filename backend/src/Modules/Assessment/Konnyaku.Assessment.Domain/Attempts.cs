using Konnyaku.SharedKernel;

namespace Konnyaku.Assessment.Domain;

public enum AttemptStatus
{
    InProgress,

    /// <summary>Handed in; waits for the teacher (essays, mismatched short answers, or teacher-graded assignments).</summary>
    Submitted,
    Graded,
}

public enum ItemGradeState
{
    /// <summary>Not graded yet (attempt in progress).</summary>
    Open,
    Auto,
    NeedsTeacher,
    Teacher,
}

/// <summary>The question as the learner got it: content (options already shuffled), key and points are frozen.</summary>
public sealed class ItemSnapshot
{
    public QuestionType Type { get; set; }
    public int Version { get; set; }
    public string Stem { get; set; } = string.Empty;
    public QuestionContent Content { get; set; } = new();
    public AnswerKey Key { get; set; } = new();
    public decimal Points { get; set; }
    public string? Explanation { get; set; }
    public Guid? GroupId { get; set; }
    public string? GroupTitle { get; set; }
    public string? Passage { get; set; }
    public string? SkillCode { get; set; }
    public string? LevelCode { get; set; }
    public Rubric? Rubric { get; set; }
}

/// <summary>A section as the learner got it (title, skill and minutes are frozen at start).</summary>
public sealed class AttemptSection
{
    public string Title { get; set; } = string.Empty;
    public string? SkillCode { get; set; }
    public int? TimeLimitMinutes { get; set; }
}

public sealed class AttemptLayout
{
    public List<AttemptSection> Sections { get; set; } = [];

    /// <summary>Sections run one after the other on their own clocks.</summary>
    public bool Timed => Sections.Count > 0 && Sections.All(s => s.TimeLimitMinutes > 0);

    public int Minutes(int fromSection) => Sections.Skip(fromSection).Sum(s => s.TimeLimitMinutes ?? 0);
}

public sealed class RubricBands
{
    public Dictionary<string, decimal> Bands { get; set; } = [];
}

/// <summary>One question of an attempt.</summary>
public sealed class AttemptItem : Entity, ITenantOwned
{
    private AttemptItem()
    {
    }

    public AttemptItem(Guid attemptId, int ordinal, int sectionIndex, Guid questionId, ItemSnapshot snapshot)
    {
        AttemptId = attemptId;
        Ordinal = ordinal;
        SectionIndex = sectionIndex;
        QuestionId = questionId;
        QuestionVersion = snapshot.Version;
        Snapshot = snapshot;
    }

    public Guid TenantId { get; private set; }
    public Guid AttemptId { get; private set; }
    public int Ordinal { get; private set; }
    public int SectionIndex { get; private set; }
    public Guid QuestionId { get; private set; }
    public int QuestionVersion { get; private set; }
    public ItemSnapshot Snapshot { get; private set; } = new();
    public ItemResponse Response { get; private set; } = new();
    public bool Flagged { get; private set; }
    public decimal? Score { get; private set; }
    public ItemGradeState State { get; private set; }
    public RubricBands Bands { get; private set; } = new();
    public string? Comment { get; private set; }
    public Guid? GradedBy { get; private set; }
    public DateTimeOffset? GradedAt { get; private set; }

    public bool NeedsTeacher => State == ItemGradeState.NeedsTeacher;

    public void Answer(ItemResponse response, bool flagged)
    {
        Response = Trim(response);
        Flagged = flagged;
    }

    public void AutoGrade()
    {
        var grade = AutoGrader.Grade(Snapshot.Type, Snapshot.Key, Response, Snapshot.Points);
        Score = grade.Score;
        State = grade.NeedsTeacher ? ItemGradeState.NeedsTeacher : ItemGradeState.Auto;
    }

    /// <summary>A teacher's score: a number, or rubric bands converted with the item's rubric.</summary>
    public Result<bool> Grade(decimal? score, IReadOnlyDictionary<string, decimal>? bands, string? comment, Guid teacherId, DateTimeOffset now)
    {
        decimal value;
        if (bands is { Count: > 0 })
        {
            if (Snapshot.Rubric?.Score(bands, Snapshot.Points) is not { } fromBands)
            {
                return Error.Validation("assessment.rubric_scores_invalid");
            }

            value = fromBands;
            Bands = new RubricBands { Bands = bands.ToDictionary() };
        }
        else if (score is { } given && given >= 0 && given <= Snapshot.Points)
        {
            value = Math.Round(given, 2);
            Bands = new RubricBands();
        }
        else
        {
            return Error.Validation("assessment.score_invalid");
        }

        Score = value;
        State = ItemGradeState.Teacher;
        Comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim()[..Math.Min(comment.Trim().Length, 4000)];
        GradedBy = teacherId;
        GradedAt = now;
        return true;
    }

    private static ItemResponse Trim(ItemResponse r) => new()
    {
        Choices = r.Choices.Take(Question.MaxOptions).Select(Cut).ToList(),
        Order = r.Order.Take(Question.MaxOptions).Select(Cut).ToList(),
        Pairs = r.Pairs.Take(Question.MaxOptions).ToDictionary(p => Cut(p.Key), p => Cut(p.Value)),
        Blanks = r.Blanks.Take(20).Select(b => b.Length > 200 ? b[..200] : b).ToList(),
        Text = r.Text is { Length: > 20_000 } text ? text[..20_000] : r.Text,
    };

    private static string Cut(string s) => s.Length > 40 ? s[..40] : s;
}

public enum AttemptEventKind
{
    TabHidden,
    Paste,
}

/// <summary>Logged for the teacher, never blocking (home tests cannot be proctored).</summary>
public sealed class AttemptEvent : Entity, ITenantOwned
{
    public const int MaxPerAttempt = 500;

    private AttemptEvent()
    {
    }

    public AttemptEvent(Guid attemptId, AttemptEventKind kind, Guid? itemId, int value, DateTimeOffset at)
    {
        AttemptId = attemptId;
        Kind = kind;
        ItemId = itemId;
        Value = Math.Clamp(value, 0, 1_000_000);
        OccurredAt = at;
    }

    public Guid TenantId { get; private set; }
    public Guid AttemptId { get; private set; }
    public AttemptEventKind Kind { get; private set; }
    public Guid? ItemId { get; private set; }

    /// <summary>Seconds the tab stayed hidden, or characters pasted.</summary>
    public int Value { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }
}

/// <summary>
/// A learner's attempt. The server owns the clock: <see cref="DeadlineAt"/> is set at start (and per section for timed
/// papers); past it the attempt is submitted by whoever touches it first (the learner's next request or the Worker).
/// </summary>
public sealed class Attempt : Entity, ITenantOwned
{
    private Attempt()
    {
    }

    public Guid TenantId { get; private set; }
    public Guid AssignmentId { get; private set; }
    public Guid StudentId { get; private set; }

    /// <summary>Learner account that took it (access check of the <c>/my</c> endpoints).</summary>
    public Guid UserId { get; private set; }

    public int Number { get; private set; }
    public AttemptStatus Status { get; private set; }
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset? DeadlineAt { get; private set; }

    public AttemptLayout Layout { get; private set; } = new();

    public int SectionIndex { get; private set; }
    public DateTimeOffset? SectionDeadlineAt { get; private set; }

    /// <summary>Hard stop of a locked assignment (its due time).</summary>
    public DateTimeOffset? LockAt { get; private set; }

    public DateTimeOffset? SubmittedAt { get; private set; }
    public bool AutoSubmitted { get; private set; }
    public bool Late { get; private set; }
    public decimal MaxScore { get; private set; }

    /// <summary>Sum of the item scores known so far.</summary>
    public decimal RawScore { get; private set; }

    /// <summary>Official score (after the late penalty), set when graded.</summary>
    public decimal? Score { get; private set; }

    public int PenaltyPercent { get; private set; }
    public int PendingItems { get; private set; }
    public DateTimeOffset? GradedAt { get; private set; }
    public Guid? GradedBy { get; private set; }
    public string? Comment { get; private set; }
    public DateTimeOffset LastSavedAt { get; private set; }

    /// <summary>PostgreSQL <c>xmin</c>: an autosave and an auto-submit racing on the same attempt cannot both win.</summary>
    public uint RowVersion { get; private set; }

    public bool Timed => Layout.Timed;

    public static Attempt Start(Assignment assignment, Guid studentId, Guid userId, int number, AttemptLayout layout, decimal maxScore,
        DateTimeOffset now)
    {
        var attempt = new Attempt
        {
            AssignmentId = assignment.Id,
            StudentId = studentId,
            UserId = userId,
            Number = number,
            Status = AttemptStatus.InProgress,
            StartedAt = now,
            LastSavedAt = now,
            MaxScore = maxScore,
            Layout = layout,
            LockAt = assignment.LatePolicy == LatePolicy.Lock ? assignment.DueAt : null,
        };
        if (attempt.Timed)
        {
            attempt.SectionDeadlineAt = Cap(now.AddMinutes(layout.Minutes(0) - layout.Minutes(1)), attempt.LockAt);
            attempt.DeadlineAt = Cap(now.AddMinutes(layout.Minutes(0)), attempt.LockAt);
        }
        else
        {
            attempt.DeadlineAt = Cap(assignment.TimeLimitMinutes is { } limit ? now.AddMinutes(limit) : null, attempt.LockAt);
        }

        return attempt;
    }

    /// <summary>The overall time is up (the attempt must be submitted before anything else).</summary>
    public bool Expired(DateTimeOffset now) => Status == AttemptStatus.InProgress && DeadlineAt is { } d && now >= d;

    /// <summary>
    /// Moves past sections whose time ran out: the next section's clock starts when the previous one ended, as in a
    /// real exam, even if the learner was away.
    /// </summary>
    public void CatchUpSections(DateTimeOffset now)
    {
        while (Status == AttemptStatus.InProgress && Timed && SectionDeadlineAt is { } end && now >= end && SectionIndex < Layout.Sections.Count - 1)
        {
            SectionIndex++;
            SectionDeadlineAt = Cap(end.AddMinutes(Limit(SectionIndex)), LockAt);
        }
    }

    /// <summary>The learner finishes the current section early; its remaining time is lost.</summary>
    public Result<bool> NextSection(DateTimeOffset now)
    {
        if (Status != AttemptStatus.InProgress)
        {
            return Error.Conflict("assessment.attempt_closed");
        }

        if (!Timed || SectionIndex >= Layout.Sections.Count - 1)
        {
            return Error.Conflict("assessment.last_section");
        }

        SectionIndex++;
        SectionDeadlineAt = Cap(now.AddMinutes(Limit(SectionIndex)), LockAt);
        DeadlineAt = Cap(now.AddMinutes(Layout.Minutes(SectionIndex)), LockAt);
        return true;
    }

    /// <summary>Whether answers of <paramref name="item"/> may still change.</summary>
    public Error? CanAnswer(AttemptItem item, DateTimeOffset now)
    {
        if (Status != AttemptStatus.InProgress || Expired(now))
        {
            return Error.Conflict("assessment.attempt_closed");
        }

        return Timed && item.SectionIndex != SectionIndex ? Error.Conflict("assessment.section_closed") : null;
    }

    public void Touch(DateTimeOffset now) => LastSavedAt = now;

    /// <summary>
    /// Hands the attempt in: machine-grades every item, then either finishes grading (auto assignment, nothing left for a
    /// teacher) or waits in the teacher's queue. An expired attempt counts as handed in at its deadline.
    /// </summary>
    public void Submit(Assignment assignment, IReadOnlyList<AttemptItem> items, DateTimeOffset now, bool auto)
    {
        if (Status != AttemptStatus.InProgress)
        {
            return;
        }

        var at = DeadlineAt is { } deadline && deadline < now ? deadline : now;
        SubmittedAt = at;
        AutoSubmitted = auto;
        Late = assignment.DueAt is { } due && at > due;
        PenaltyPercent = Late && assignment.LatePolicy == LatePolicy.Penalty ? assignment.LatePenaltyPercent : 0;
        foreach (var item in items)
        {
            item.AutoGrade();
        }

        Status = AttemptStatus.Submitted;
        Recompute(items);
        if (assignment.Grader == GraderMode.Auto && PendingItems == 0)
        {
            Finish(null, null, at);
        }
    }

    /// <summary>Scores after a teacher changed an item (a graded attempt stays graded, with the new score).</summary>
    public void Recompute(IReadOnlyList<AttemptItem> items)
    {
        RawScore = items.Sum(i => i.Score ?? 0);
        PendingItems = items.Count(i => i.NeedsTeacher);
        if (Status == AttemptStatus.Graded)
        {
            Score = Penalized(RawScore);
        }
    }

    public Result<bool> Finalize(IReadOnlyList<AttemptItem> items, string? comment, Guid teacherId, DateTimeOffset now)
    {
        if (Status == AttemptStatus.InProgress)
        {
            return Error.Conflict("assessment.attempt_not_submitted");
        }

        Recompute(items);
        if (PendingItems > 0)
        {
            return Error.Conflict("assessment.grading_incomplete");
        }

        Finish(comment, teacherId, now);
        return true;
    }

    private void Finish(string? comment, Guid? teacherId, DateTimeOffset now)
    {
        Status = AttemptStatus.Graded;
        Score = Penalized(RawScore);
        GradedAt = now;
        GradedBy = teacherId ?? GradedBy;
        if (comment is not null)
        {
            Comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim()[..Math.Min(comment.Trim().Length, 4000)];
        }
    }

    private int Limit(int section) => Layout.Sections[section].TimeLimitMinutes ?? 0;

    private decimal Penalized(decimal raw) => Math.Round(raw * (100 - PenaltyPercent) / 100m, 2);

    private static DateTimeOffset? Cap(DateTimeOffset? value, DateTimeOffset? cap) =>
        (value, cap) switch
        {
            (null, _) => cap,
            (_, null) => value,
            var (v, c) => v < c ? v : c,
        };
}
