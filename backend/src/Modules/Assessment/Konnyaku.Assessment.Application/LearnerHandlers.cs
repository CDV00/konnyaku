using Konnyaku.Application.Persistence;
using Konnyaku.Assessment.Domain;
using Konnyaku.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Konnyaku.Assessment.Application;

/// <summary>Where a learner stands on an assignment.</summary>
public enum LearnerState
{
    Upcoming,
    Open,
    InProgress,
    Submitted,
    Graded,
    Closed,
}

public sealed record MyAssignmentView(
    Guid Id, string Title, AssignmentKind Kind, Guid ClassId, DateTimeOffset OpensAt, DateTimeOffset? DueAt, LatePolicy LatePolicy,
    int? MaxAttempts, int AttemptsUsed, int? TimeLimitMinutes, LearnerState State, Guid? OpenAttemptId, Guid? LastAttemptId, bool ScoreVisible,
    decimal? Score, decimal? MaxScore, bool CanStart);

public sealed record MyAttemptSummary(Guid Id, int Number, AttemptStatus Status, DateTimeOffset StartedAt, DateTimeOffset? SubmittedAt, bool Late,
    bool ScoreVisible, decimal? Score, decimal MaxScore);

public sealed record MyAssignmentDetail(MyAssignmentView Assignment, ScoringRule ScoringRule, ScoreRelease ScoreRelease, DateTimeOffset? ReleaseAt,
    AnswerVisibility AnswerVisibility, int? PaperMinutes, int PaperQuestions, IReadOnlyList<MyAttemptSummary> Attempts);

public sealed record AnswerEntry(Guid ItemId, ItemResponse? Response, bool Flagged);

public sealed record SaveAnswersCommand(IReadOnlyList<AnswerEntry> Answers);

public sealed record SaveResult(DateTimeOffset SavedAt, DateTimeOffset ServerNow, int SectionIndex, DateTimeOffset? DeadlineAt, AttemptStatus Status);

public sealed record AttemptEventCommand(AttemptEventKind Kind, Guid? ItemId, int Value);

/// <summary>The learner side: my assignments, start or resume, autosave, hand in, results.</summary>
public sealed class LearnerHandlers(IAssessmentDbContext db, LearnerScope scope, AttemptService attempts, IDbErrorClassifier errors, TimeProvider clock)
{
    public const int MaxAnswersPerSave = 300;

    private static readonly Error AssignmentNotFound = Error.NotFound("assessment.assignment_not_found");
    private static readonly Error AttemptNotFound = Error.NotFound("assessment.attempt_not_found");

    public async Task<IReadOnlyList<MyAssignmentView>> ListAsync(Guid userId, CancellationToken ct)
    {
        var (students, classes) = await scope.OfUserAsync(userId, ct);
        if (students.Count == 0)
        {
            return [];
        }

        var classIds = classes.ToArray();
        var candidates = await db.Assignments.AsNoTracking().Where(a => classIds.Contains(a.ClassId))
            .OrderByDescending(a => a.DueAt ?? a.OpensAt).Take(300).ToListAsync(ct);
        var ids = candidates.Select(a => a.Id).ToArray();
        var mine = await db.Attempts.AsNoTracking().Where(t => ids.Contains(t.AssignmentId) && t.UserId == userId).ToListAsync(ct);
        var now = clock.GetUtcNow();
        var views = new List<MyAssignmentView>();
        foreach (var assignment in candidates)
        {
            if (await scope.StudentForAsync(assignment, students, ct) is not { } studentId)
            {
                continue;
            }

            views.Add(ToView(assignment, mine.Where(t => t.AssignmentId == assignment.Id && t.StudentId == studentId).OrderBy(t => t.Number).ToList(), now));
        }

        return views;
    }

    public async Task<Result<MyAssignmentDetail>> GetAsync(Guid assignmentId, Guid userId, CancellationToken ct)
    {
        var (assignment, studentId) = await MineAsync(assignmentId, userId, ct);
        if (assignment is null)
        {
            return AssignmentNotFound;
        }

        var list = await db.Attempts.AsNoTracking().Where(t => t.AssignmentId == assignmentId && t.StudentId == studentId && t.UserId == userId)
            .OrderBy(t => t.Number).ToListAsync(ct);
        var paper = await db.Papers.AsNoTracking().FirstAsync(p => p.Id == assignment.PaperId, ct);
        var now = clock.GetUtcNow();
        var groupIds = paper.Layout.Sections.SelectMany(s => s.Items).Where(i => i.Kind == SectionItemKind.Group && i.GroupId is not null)
            .Select(i => i.GroupId!.Value).ToArray();
        var grouped = groupIds.Length == 0 ? 0 : await db.Questions.CountAsync(q => q.GroupId != null && groupIds.Contains(q.GroupId.Value) && !q.Archived, ct);
        var questions = paper.Layout.Sections.SelectMany(s => s.Items).Sum(i => i.Kind switch
        {
            SectionItemKind.Random => i.Count,
            SectionItemKind.Group => 0,
            _ => 1,
        }) + grouped;
        return new MyAssignmentDetail(ToView(assignment, list, now), assignment.ScoringRule, assignment.ScoreRelease, assignment.ReleaseAt,
            assignment.AnswerVisibility, paper.Layout.Timed ? paper.Layout.Sections.Sum(s => s.TimeLimitMinutes ?? 0) : assignment.TimeLimitMinutes,
            questions, list.Select(t => Summary(assignment, t, now)).ToList());
    }

    /// <summary>Starts an attempt, or returns the open one (only one attempt is open per assignment).</summary>
    public async Task<Result<AttemptPlayView>> StartAsync(Guid assignmentId, Guid userId, CancellationToken ct)
    {
        var (assignment, studentId) = await MineAsync(assignmentId, userId, ct);
        if (assignment is null)
        {
            return AssignmentNotFound;
        }

        var now = clock.GetUtcNow();
        var open = await db.Attempts.FirstOrDefaultAsync(t => t.AssignmentId == assignmentId && t.StudentId == studentId && t.Status == AttemptStatus.InProgress, ct);
        if (open is not null && !await attempts.SubmitIfExpiredAsync(open, ct))
        {
            return await PlayAsync(open, assignment, ct);
        }

        if (now < assignment.OpensAt)
        {
            return Error.Conflict("assessment.not_open");
        }

        if (assignment.IsClosed(now))
        {
            return Error.Conflict("assessment.assignment_closed");
        }

        var used = await db.Attempts.CountAsync(t => t.AssignmentId == assignmentId && t.StudentId == studentId, ct);
        if (assignment.MaxAttempts is { } max && used >= max)
        {
            return Error.Conflict("assessment.attempts_exhausted");
        }

        var paper = await db.Papers.AsNoTracking().FirstAsync(p => p.Id == assignment.PaperId, ct);
        var (layout, drawn) = await attempts.DrawAsync(paper, ct);
        if (drawn.Count == 0)
        {
            return Error.Conflict("assessment.paper_empty");
        }

        var snapshots = drawn.Select(d => (d.Section, d.Question.Id, Snapshot: AttemptService.Snapshot(paper, layout.Sections[d.Section], d.Question, d.Group, d.Item)))
            .ToList();
        var attempt = Attempt.Start(assignment, studentId, userId, used + 1, layout, snapshots.Sum(s => s.Snapshot.Points), now);
        db.Attempts.Add(attempt);
        var ordinal = 0;
        foreach (var (section, questionId, snapshot) in snapshots)
        {
            db.AttemptItems.Add(new AttemptItem(attempt.Id, ++ordinal, section, questionId, snapshot));
        }

        if (!await db.TrySaveAsync(errors, ct))
        {
            // Another tab started it a moment ago: resume that one.
            db.Reset();
            var winner = await db.Attempts.FirstOrDefaultAsync(t => t.AssignmentId == assignmentId && t.StudentId == studentId && t.Status == AttemptStatus.InProgress, ct);
            return winner is null ? Error.Conflict("assessment.attempt_conflict") : await PlayAsync(winner, assignment, ct);
        }

        return await PlayAsync(attempt, assignment, ct);
    }

    /// <summary>The attempt to work on (sections whose time ran out are passed; an expired attempt is handed in first).</summary>
    public async Task<Result<AttemptPlayView>> GetAttemptAsync(Guid attemptId, Guid userId, CancellationToken ct)
    {
        var (attempt, assignment) = await AttemptOfAsync(attemptId, userId, ct);
        if (attempt is null)
        {
            return AttemptNotFound;
        }

        await attempts.SubmitIfExpiredAsync(attempt, ct);
        return await PlayAsync(attempt, assignment!, ct);
    }

    /// <summary>Autosave: answers and review flags of items of the current section.</summary>
    public async Task<Result<SaveResult>> SaveAsync(Guid attemptId, Guid userId, SaveAnswersCommand command, CancellationToken ct)
    {
        if (command.Answers is not { Count: > 0 and <= MaxAnswersPerSave })
        {
            return Error.Validation("assessment.answers_invalid");
        }

        var (attempt, _) = await AttemptOfAsync(attemptId, userId, ct);
        if (attempt is null)
        {
            return AttemptNotFound;
        }

        if (await attempts.SubmitIfExpiredAsync(attempt, ct))
        {
            return Error.Conflict("assessment.attempt_closed");
        }

        var now = clock.GetUtcNow();
        attempt.CatchUpSections(now);
        var ids = command.Answers.Select(a => a.ItemId).Distinct().ToArray();
        var items = await db.AttemptItems.Where(i => i.AttemptId == attemptId && ids.Contains(i.Id)).ToListAsync(ct);
        if (items.Count != ids.Length)
        {
            return Error.Validation("assessment.item_not_found");
        }

        foreach (var entry in command.Answers)
        {
            var item = items.First(i => i.Id == entry.ItemId);
            if (attempt.CanAnswer(item, now) is { } closed)
            {
                return closed;
            }

            item.Answer(entry.Response ?? new ItemResponse(), entry.Flagged);
        }

        attempt.Touch(now);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.Reset();
            return Error.Conflict("assessment.attempt_changed");
        }

        return new SaveResult(now, now, attempt.SectionIndex, attempt.DeadlineAt, attempt.Status);
    }

    public async Task<Result<AttemptPlayView>> NextSectionAsync(Guid attemptId, Guid userId, CancellationToken ct)
    {
        var (attempt, assignment) = await AttemptOfAsync(attemptId, userId, ct);
        if (attempt is null)
        {
            return AttemptNotFound;
        }

        if (await attempts.SubmitIfExpiredAsync(attempt, ct))
        {
            return Error.Conflict("assessment.attempt_closed");
        }

        var now = clock.GetUtcNow();
        attempt.CatchUpSections(now);
        var moved = attempt.NextSection(now);
        if (!moved.IsSuccess)
        {
            return moved.Error!;
        }

        await db.SaveChangesAsync(ct);
        return await PlayAsync(attempt, assignment!, ct);
    }

    public async Task<Result<AttemptResultView>> SubmitAsync(Guid attemptId, Guid userId, CancellationToken ct)
    {
        var (attempt, assignment) = await AttemptOfAsync(attemptId, userId, ct);
        if (attempt is null)
        {
            return AttemptNotFound;
        }

        if (attempt.Status == AttemptStatus.InProgress)
        {
            var items = await db.AttemptItems.Where(i => i.AttemptId == attemptId).OrderBy(i => i.Ordinal).ToListAsync(ct);
            attempt.Submit(assignment!, items, clock.GetUtcNow(), auto: attempt.Expired(clock.GetUtcNow()));
            attempts.AnnounceIfGraded(attempt, assignment!, items);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                // The Worker handed it in at the deadline meanwhile: show that result.
                db.Reset();
                (attempt, assignment) = await AttemptOfAsync(attemptId, userId, ct);
            }
        }

        return await ResultAsync(attempt!, assignment!, ct);
    }

    public async Task<Result<AttemptResultView>> GetResultAsync(Guid attemptId, Guid userId, CancellationToken ct)
    {
        var (attempt, assignment) = await AttemptOfAsync(attemptId, userId, ct);
        if (attempt is null)
        {
            return AttemptNotFound;
        }

        await attempts.SubmitIfExpiredAsync(attempt, ct);
        return attempt.Status == AttemptStatus.InProgress ? Error.Conflict("assessment.attempt_not_submitted") : await ResultAsync(attempt, assignment!, ct);
    }

    /// <summary>Tab hidden / paste: logged for the teacher, never blocking.</summary>
    public async Task<Result<bool>> LogEventAsync(Guid attemptId, Guid userId, AttemptEventCommand command, CancellationToken ct)
    {
        var (attempt, _) = await AttemptOfAsync(attemptId, userId, ct);
        if (attempt is null)
        {
            return AttemptNotFound;
        }

        if (attempt.Status != AttemptStatus.InProgress || await db.AttemptEvents.CountAsync(e => e.AttemptId == attemptId, ct) >= AttemptEvent.MaxPerAttempt)
        {
            return true;
        }

        if (command.ItemId is { } itemId && !await db.AttemptItems.AnyAsync(i => i.Id == itemId && i.AttemptId == attemptId, ct))
        {
            return Error.Validation("assessment.item_not_found");
        }

        db.AttemptEvents.Add(new AttemptEvent(attemptId, command.Kind, command.ItemId, command.Value, clock.GetUtcNow()));
        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<AttemptPlayView> PlayAsync(Attempt attempt, Assignment assignment, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        attempt.CatchUpSections(now);
        var items = await db.AttemptItems.AsNoTracking().Where(i => i.AttemptId == attempt.Id).OrderBy(i => i.Ordinal).ToListAsync(ct);
        var showRuby = await db.Papers.Where(p => p.Id == assignment.PaperId).Select(p => p.ShowRuby).FirstAsync(ct);
        return new AttemptPlayView(attempt.Id, assignment.Id, assignment.Title, assignment.Kind, attempt.Number, attempt.Status, attempt.StartedAt,
            attempt.DeadlineAt, attempt.SectionIndex, attempt.SectionDeadlineAt, attempt.Layout.Sections, attempt.Timed, showRuby, now,
            attempt.Status == AttemptStatus.InProgress ? items.Select(i => AttemptViews.Play(i, attempt)).ToList() : []);
    }

    private async Task<AttemptResultView> ResultAsync(Attempt attempt, Assignment assignment, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var scores = ReleasePolicy.ScoresVisible(assignment, attempt, now);
        var answers = ReleasePolicy.AnswersVisible(assignment, attempt, now);
        var items = await db.AttemptItems.AsNoTracking().Where(i => i.AttemptId == attempt.Id).OrderBy(i => i.Ordinal).ToListAsync(ct);
        var showRuby = await db.Papers.Where(p => p.Id == assignment.PaperId).Select(p => p.ShowRuby).FirstAsync(ct);
        return new AttemptResultView(attempt.Id, assignment.Id, assignment.Title, assignment.Kind, attempt.Number, attempt.Status, attempt.StartedAt,
            attempt.SubmittedAt, attempt.Late, attempt.AutoSubmitted, scores, answers, assignment.ScoreRelease == ScoreRelease.AtTime ? assignment.ReleaseAt : null,
            scores && attempt.Status == AttemptStatus.Graded ? attempt.Score : null, scores ? attempt.RawScore : null, attempt.MaxScore,
            attempt.PenaltyPercent, scores ? attempt.PendingItems : 0, scores ? attempt.Comment : null, scores ? AttemptViews.Skills(items) : [],
            showRuby, attempt.Layout.Sections, items.Select(i => AttemptViews.Result(i, scores, answers)).ToList());
    }

    /// <summary>The assignment, when it is for one of the learner's student profiles (in the class roster, targeted).</summary>
    private async Task<(Assignment? Assignment, Guid StudentId)> MineAsync(Guid assignmentId, Guid userId, CancellationToken ct)
    {
        var assignment = await db.Assignments.FirstOrDefaultAsync(a => a.Id == assignmentId, ct);
        if (assignment is null)
        {
            return (null, Guid.Empty);
        }

        var (students, classes) = await scope.OfUserAsync(userId, ct);
        if (!classes.Contains(assignment.ClassId) || await scope.StudentForAsync(assignment, students, ct) is not { } studentId)
        {
            return (null, Guid.Empty);
        }

        return (assignment, studentId);
    }

    /// <summary>An attempt of the signed-in learner (anyone else's answers not found).</summary>
    private async Task<(Attempt? Attempt, Assignment? Assignment)> AttemptOfAsync(Guid attemptId, Guid userId, CancellationToken ct)
    {
        var attempt = await db.Attempts.FirstOrDefaultAsync(t => t.Id == attemptId && t.UserId == userId, ct);
        if (attempt is null)
        {
            return (null, null);
        }

        return (attempt, await db.Assignments.FirstAsync(a => a.Id == attempt.AssignmentId, ct));
    }

    private static MyAssignmentView ToView(Assignment a, IReadOnlyList<Attempt> mine, DateTimeOffset now)
    {
        var open = mine.FirstOrDefault(t => t.Status == AttemptStatus.InProgress);
        var last = mine.LastOrDefault(t => t.Status != AttemptStatus.InProgress);
        var visible = mine.Where(t => t.Status == AttemptStatus.Graded && ReleasePolicy.ScoresVisible(a, t, now)).ToList();
        var exhausted = a.MaxAttempts is { } max && mine.Count >= max;
        var state = open is not null ? LearnerState.InProgress
            : now < a.OpensAt ? LearnerState.Upcoming
            : last is not null ? (last.Status == AttemptStatus.Graded ? LearnerState.Graded : LearnerState.Submitted)
            : a.IsClosed(now) ? LearnerState.Closed
            : LearnerState.Open;
        var canStart = open is null && now >= a.OpensAt && !a.IsClosed(now) && !exhausted;
        return new MyAssignmentView(a.Id, a.Title, a.Kind, a.ClassId, a.OpensAt, a.DueAt, a.LatePolicy, a.MaxAttempts, mine.Count, a.TimeLimitMinutes, state,
            open?.Id, last?.Id, visible.Count > 0, ReleasePolicy.FinalScore(a.ScoringRule, visible.Select(t => t.Score!.Value).ToList()),
            visible.Count > 0 ? visible[^1].MaxScore : null, canStart);
    }

    private static MyAttemptSummary Summary(Assignment a, Attempt t, DateTimeOffset now)
    {
        var visible = ReleasePolicy.ScoresVisible(a, t, now);
        return new MyAttemptSummary(t.Id, t.Number, t.Status, t.StartedAt, t.SubmittedAt, t.Late, visible,
            visible && t.Status == AttemptStatus.Graded ? t.Score : null, t.MaxScore);
    }
}
