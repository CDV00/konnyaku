using Konnyaku.Academic.Contracts;
using Konnyaku.Assessment.Contracts;
using Konnyaku.Assessment.Domain;
using Konnyaku.Crm.Contracts;
using Konnyaku.Scheduling.Contracts;
using Konnyaku.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Konnyaku.Assessment.Application;

public sealed record AssignmentCommand(
    Guid PaperId, string Title, AssignmentTarget Target, Guid? ClassId, Guid? SessionId, IReadOnlyList<Guid>? StudentIds, AssignmentKind Kind,
    DateTimeOffset? OpensAt, DateTimeOffset? DueAt, LatePolicy LatePolicy, int LatePenaltyPercent, int? MaxAttempts, ScoringRule ScoringRule,
    GraderMode Grader, ScoreRelease ScoreRelease, DateTimeOffset? ReleaseAt, AnswerVisibility AnswerVisibility, int? TimeLimitMinutes);

public sealed record UpdateAssignmentCommand(
    string Title, AssignmentKind Kind, DateTimeOffset OpensAt, DateTimeOffset? DueAt, LatePolicy LatePolicy, int LatePenaltyPercent, int? MaxAttempts,
    ScoringRule ScoringRule, GraderMode Grader, ScoreRelease ScoreRelease, DateTimeOffset? ReleaseAt, AnswerVisibility AnswerVisibility,
    int? TimeLimitMinutes);

public sealed record AssignmentStats(int Students, int Started, int Submitted, int Graded, int AwaitingGrading);

public sealed record AssignmentView(
    Guid Id, Guid PaperId, string PaperTitle, string Title, AssignmentTarget Target, Guid ClassId, string ClassCode, Guid BranchId, Guid? SessionId,
    IReadOnlyList<Guid> StudentIds, AssignmentKind Kind, DateTimeOffset OpensAt, DateTimeOffset? DueAt, LatePolicy LatePolicy, int LatePenaltyPercent,
    int? MaxAttempts, ScoringRule ScoringRule, GraderMode Grader, ScoreRelease ScoreRelease, DateTimeOffset? ReleaseAt,
    AnswerVisibility AnswerVisibility, int? TimeLimitMinutes, Guid CreatedBy, DateTimeOffset CreatedAt, AssignmentStats Stats);

public sealed record StudentAttemptRow(
    Guid StudentId, string StudentName, int Attempts, AttemptStatus? LastStatus, Guid? LastAttemptId, DateTimeOffset? LastSubmittedAt, bool Late,
    decimal? FinalScore, decimal? MaxScore, int PendingItems, int Flags);

public sealed record AssignmentDetail(AssignmentView Assignment, IReadOnlyList<StudentAttemptRow> Students);

public sealed record AssignmentFilter(Guid? ClassId = null, AssignmentKind? Kind = null, string? Status = null);

public sealed record GradingFilter(Guid? ClassId = null, Guid? AssignmentId = null, DateTimeOffset? DueFrom = null, DateTimeOffset? DueTo = null);

public sealed record GradingQueueItem(
    Guid AttemptId, Guid AssignmentId, string AssignmentTitle, AssignmentKind Kind, Guid ClassId, string ClassCode, Guid StudentId,
    string StudentName, int Number, DateTimeOffset? SubmittedAt, DateTimeOffset? DueAt, bool Late, int PendingItems, decimal RawScore,
    decimal MaxScore);

public sealed record GradeItemCommand(decimal? Score, IReadOnlyDictionary<string, decimal>? Bands, string? Comment);

public sealed record FinalizeCommand(string? Comment);

public sealed record OverviewView(
    int Assignments, int OpenNow, int AwaitingGrading, int GradedLast30Days, decimal? AveragePercent,
    IReadOnlyList<KindCount> ByKind, IReadOnlyList<AssignmentView> Recent);

public sealed record KindCount(AssignmentKind Kind, int Count);

public sealed record ClassStudent(Guid StudentId, string Name);

/// <summary>Assignments, the grading queue and results for teachers and managers.</summary>
public sealed class AssignmentHandlers(
    IAssessmentDbContext db, AssessmentAccess access, AttemptService attempts, IAcademicApi academic, ISchedulingApi scheduling, ICrmApi crm,
    TimeProvider clock)
{
    private static readonly Error NotFound = Error.NotFound("assessment.assignment_not_found");

    public async Task<IReadOnlyList<AssignmentView>> ListAsync(Actor actor, AssignmentFilter filter, CancellationToken ct)
    {
        var query = await access.VisibleAssignmentsAsync(db.Assignments.AsNoTracking(), actor, ct);
        if (filter.ClassId is { } classId)
        {
            query = query.Where(a => a.ClassId == classId);
        }

        if (filter.Kind is { } kind)
        {
            query = query.Where(a => a.Kind == kind);
        }

        var now = clock.GetUtcNow();
        query = filter.Status switch
        {
            "upcoming" => query.Where(a => a.OpensAt > now),
            "open" => query.Where(a => a.OpensAt <= now && (a.DueAt == null || a.DueAt > now)),
            "past" => query.Where(a => a.DueAt != null && a.DueAt <= now),
            _ => query,
        };
        var rows = await query.OrderByDescending(a => a.OpensAt).Take(300).ToListAsync(ct);
        return await ViewsAsync(rows, ct);
    }

    public async Task<Result<AssignmentView>> CreateAsync(AssignmentCommand c, Actor actor, CancellationToken ct)
    {
        var paper = await db.Papers.AsNoTracking().FirstOrDefaultAsync(p => p.Id == c.PaperId && !p.Archived, ct);
        if (paper is null || !await access.CanAuthorAsync(actor, ct))
        {
            return Error.Validation("assessment.paper_not_found");
        }

        var classId = c.ClassId;
        if (c.Target == AssignmentTarget.Session)
        {
            var session = c.SessionId is { } sessionId ? await scheduling.GetSessionAsync(sessionId, ct) : null;
            if (session is null || (classId is not null && session.ClassId != classId))
            {
                return Error.Validation("assessment.session_not_found");
            }

            classId = session.ClassId;
        }

        var cls = classId is { } id ? await academic.GetClassAsync(id, ct) : null;
        if (cls is null)
        {
            return Error.Validation("assessment.class_not_found");
        }

        if (!await access.CanManageClassAsync(cls.Id, cls.BranchId, actor, ct))
        {
            return AssessmentAccess.Forbidden;
        }

        var students = c.StudentIds ?? [];
        if (c.Target == AssignmentTarget.Students)
        {
            var roster = (await academic.GetRosterAsync(cls.Id, ct)).Select(r => r.StudentId).ToHashSet();
            if (students.Count == 0 || !students.All(roster.Contains))
            {
                return Error.Validation("assessment.students_not_in_class");
            }
        }

        var created = Assignment.Create(paper.Id, string.IsNullOrWhiteSpace(c.Title) ? paper.Title : c.Title, c.Target, cls.Id, cls.BranchId, c.SessionId,
            students, Settings(c.Kind, c.OpensAt ?? clock.GetUtcNow(), c.DueAt, c.LatePolicy, c.LatePenaltyPercent, c.MaxAttempts, c.ScoringRule,
                c.Grader, c.ScoreRelease, c.ReleaseAt, c.AnswerVisibility, c.TimeLimitMinutes), actor.UserId, clock.GetUtcNow());
        if (!created.IsSuccess)
        {
            return created.Error!;
        }

        db.Assignments.Add(created.Value);
        db.Publish(new AssignmentPublishedV1(created.Value.Id, created.Value.ClassId, created.Value.OpensAt, created.Value.DueAt));
        await db.SaveChangesAsync(ct);
        return (await ViewsAsync([created.Value], ct))[0];
    }

    /// <summary>Settings may change any time; started attempts keep their deadline.</summary>
    public async Task<Result<AssignmentView>> UpdateAsync(Guid id, UpdateAssignmentCommand c, Actor actor, CancellationToken ct)
    {
        var (assignment, error) = await ManagedAsync(id, actor, ct);
        if (error is not null)
        {
            return error;
        }

        var (opensAt, dueAt) = (assignment!.OpensAt, assignment.DueAt);
        var updated = assignment.Update(c.Title, Settings(c.Kind, c.OpensAt, c.DueAt, c.LatePolicy, c.LatePenaltyPercent, c.MaxAttempts, c.ScoringRule,
            c.Grader, c.ScoreRelease, c.ReleaseAt, c.AnswerVisibility, c.TimeLimitMinutes), clock.GetUtcNow());
        if (!updated.IsSuccess)
        {
            return updated.Error!;
        }

        if (assignment.OpensAt != opensAt || assignment.DueAt != dueAt)
        {
            db.Publish(new AssignmentRescheduledV1(assignment.Id, assignment.OpensAt, assignment.DueAt));
        }

        await db.SaveChangesAsync(ct);
        return (await ViewsAsync([assignment], ct))[0];
    }

    /// <summary>Releases the scores to the class now (an assignment released at a set time, or after grading).</summary>
    public async Task<Result<AssignmentView>> ReleaseAsync(Guid id, Actor actor, CancellationToken ct)
    {
        var (assignment, error) = await ManagedAsync(id, actor, ct);
        if (error is not null)
        {
            return error;
        }

        assignment!.ReleaseNow(clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return (await ViewsAsync([assignment], ct))[0];
    }

    /// <summary>Deleting is only for a mistake: an assignment nobody started.</summary>
    public async Task<Result<bool>> DeleteAsync(Guid id, Actor actor, CancellationToken ct)
    {
        var (assignment, error) = await ManagedAsync(id, actor, ct);
        if (error is not null)
        {
            return error;
        }

        if (await db.Attempts.AnyAsync(t => t.AssignmentId == id, ct))
        {
            return Error.Conflict("assessment.assignment_started");
        }

        db.Assignments.Remove(assignment!);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<Result<AssignmentDetail>> GetAsync(Guid id, Actor actor, CancellationToken ct)
    {
        var (assignment, error) = await ManagedAsync(id, actor, ct);
        if (error is not null)
        {
            return error;
        }

        var view = (await ViewsAsync([assignment!], ct))[0];
        var roster = (await academic.GetRosterAsync(assignment!.ClassId, ct)).Select(r => r.StudentId).Where(assignment.IsFor).ToList();
        var list = await db.Attempts.AsNoTracking().Where(t => t.AssignmentId == id).OrderBy(t => t.Number).ToListAsync(ct);
        var attemptIds = list.Select(t => t.Id).ToArray();
        var flags = await db.AttemptEvents.AsNoTracking().Where(e => attemptIds.Contains(e.AttemptId))
            .GroupBy(e => e.AttemptId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var rows = new List<StudentAttemptRow>();
        foreach (var studentId in roster.Union(list.Select(t => t.StudentId)))
        {
            var mine = list.Where(t => t.StudentId == studentId).ToList();
            var last = mine.LastOrDefault();
            var graded = mine.Where(t => t.Status == AttemptStatus.Graded).Select(t => t.Score!.Value).ToList();
            rows.Add(new StudentAttemptRow(studentId, await NameAsync(studentId, ct), mine.Count, last?.Status, last?.Id, last?.SubmittedAt, last?.Late ?? false,
                ReleasePolicy.FinalScore(assignment.ScoringRule, graded), last?.MaxScore, mine.Sum(t => t.Status == AttemptStatus.Submitted ? Math.Max(1, t.PendingItems) : 0),
                flags.Where(f => mine.Any(t => t.Id == f.Key)).Sum(f => f.Count)));
        }

        return new AssignmentDetail(view, rows.OrderBy(r => r.StudentName, StringComparer.CurrentCulture).ToList());
    }

    /// <summary>Learners of a class to pick from when assigning to some of them (names only; teachers cannot read the student list).</summary>
    public async Task<Result<IReadOnlyList<ClassStudent>>> StudentsAsync(Guid classId, Actor actor, CancellationToken ct)
    {
        var cls = await academic.GetClassAsync(classId, ct);
        if (cls is null || !await access.CanManageClassAsync(cls.Id, cls.BranchId, actor, ct))
        {
            return Error.NotFound("assessment.class_not_found");
        }

        var students = new List<ClassStudent>();
        foreach (var entry in await academic.GetRosterAsync(classId, ct))
        {
            students.Add(new ClassStudent(entry.StudentId, await NameAsync(entry.StudentId, ct)));
        }

        return students.OrderBy(s => s.Name, StringComparer.CurrentCulture).ToList();
    }

    /// <summary>Attempts waiting for a teacher, oldest due first.</summary>
    public async Task<IReadOnlyList<GradingQueueItem>> QueueAsync(Actor actor, GradingFilter filter, CancellationToken ct)
    {
        var assignments = await access.VisibleAssignmentsAsync(db.Assignments.AsNoTracking(), actor, ct);
        if (filter.ClassId is { } classId)
        {
            assignments = assignments.Where(a => a.ClassId == classId);
        }

        if (filter.AssignmentId is { } assignmentId)
        {
            assignments = assignments.Where(a => a.Id == assignmentId);
        }

        if (filter.DueFrom is { } from)
        {
            assignments = assignments.Where(a => a.DueAt >= from);
        }

        if (filter.DueTo is { } to)
        {
            assignments = assignments.Where(a => a.DueAt <= to);
        }

        var rows = await (from t in db.Attempts.AsNoTracking()
                          join a in assignments on t.AssignmentId equals a.Id
                          where t.Status == AttemptStatus.Submitted
                          orderby a.DueAt, t.SubmittedAt
                          select new { Attempt = t, Assignment = a }).Take(500).ToListAsync(ct);
        var codes = new Dictionary<Guid, string>();
        var queue = new List<GradingQueueItem>();
        foreach (var row in rows)
        {
            queue.Add(new GradingQueueItem(row.Attempt.Id, row.Assignment.Id, row.Assignment.Title, row.Assignment.Kind, row.Assignment.ClassId,
                await ClassCodeAsync(row.Assignment.ClassId, codes, ct), row.Attempt.StudentId, await NameAsync(row.Attempt.StudentId, ct), row.Attempt.Number,
                row.Attempt.SubmittedAt, row.Assignment.DueAt, row.Attempt.Late, row.Attempt.PendingItems, row.Attempt.RawScore, row.Attempt.MaxScore));
        }

        return queue;
    }

    public async Task<Result<AttemptReview>> ReviewAsync(Guid attemptId, Actor actor, CancellationToken ct)
    {
        var (attempt, assignment, error) = await ManagedAttemptAsync(attemptId, actor, ct);
        if (error is not null)
        {
            return error;
        }

        if (await attempts.SubmitIfExpiredAsync(attempt!, ct))
        {
            (attempt, assignment, _) = await ManagedAttemptAsync(attemptId, actor, ct);
        }

        return await ReviewViewAsync(attempt!, assignment!, ct);
    }

    public async Task<Result<AttemptReview>> GradeItemAsync(Guid attemptId, Guid itemId, GradeItemCommand command, Actor actor, CancellationToken ct)
    {
        var (attempt, assignment, error) = await ManagedAttemptAsync(attemptId, actor, ct);
        if (error is not null)
        {
            return error;
        }

        if (attempt!.Status == AttemptStatus.InProgress)
        {
            return Error.Conflict("assessment.attempt_not_submitted");
        }

        var items = await db.AttemptItems.Where(i => i.AttemptId == attemptId).OrderBy(i => i.Ordinal).ToListAsync(ct);
        if (items.FirstOrDefault(i => i.Id == itemId) is not { } item)
        {
            return Error.NotFound("assessment.item_not_found");
        }

        var graded = item.Grade(command.Score, command.Bands, command.Comment, actor.UserId, clock.GetUtcNow());
        if (!graded.IsSuccess)
        {
            return graded.Error!;
        }

        attempt.Recompute(items);

        // A regrade of a graded attempt changes the official score: announce it again.
        attempts.AnnounceIfGraded(attempt, assignment!, items);
        await db.SaveChangesAsync(ct);
        return await ReviewViewAsync(attempt, assignment!, ct);
    }

    public async Task<Result<AttemptReview>> FinalizeAsync(Guid attemptId, FinalizeCommand command, Actor actor, CancellationToken ct)
    {
        var (attempt, assignment, error) = await ManagedAttemptAsync(attemptId, actor, ct);
        if (error is not null)
        {
            return error;
        }

        var items = await db.AttemptItems.Where(i => i.AttemptId == attemptId).OrderBy(i => i.Ordinal).ToListAsync(ct);
        var done = attempt!.Finalize(items, command.Comment ?? string.Empty, actor.UserId, clock.GetUtcNow());
        if (!done.IsSuccess)
        {
            return done.Error!;
        }

        attempts.AnnounceIfGraded(attempt, assignment!, items);
        await db.SaveChangesAsync(ct);
        return await ReviewViewAsync(attempt, assignment!, ct);
    }

    /// <summary>Read-only figures for managers (ops): assignments of the branches they manage.</summary>
    public async Task<OverviewView> OverviewAsync(Actor actor, CancellationToken ct)
    {
        var visible = await access.VisibleAssignmentsAsync(db.Assignments.AsNoTracking(), actor, ct);
        var now = clock.GetUtcNow();
        var since = now.AddDays(-30);
        var total = await visible.CountAsync(ct);
        var open = await visible.CountAsync(a => a.OpensAt <= now && (a.DueAt == null || a.DueAt > now), ct);
        var byKind = await visible.GroupBy(a => a.Kind).Select(g => new KindCount(g.Key, g.Count())).ToListAsync(ct);
        var attemptsOf = from t in db.Attempts.AsNoTracking() join a in visible on t.AssignmentId equals a.Id select t;
        var waiting = await attemptsOf.CountAsync(t => t.Status == AttemptStatus.Submitted, ct);
        var recentGraded = await attemptsOf.Where(t => t.Status == AttemptStatus.Graded && t.GradedAt >= since && t.MaxScore > 0)
            .Select(t => new { t.Score, t.MaxScore }).ToListAsync(ct);
        decimal? average = recentGraded.Count == 0 ? null : Math.Round(recentGraded.Average(g => (g.Score ?? 0) * 100 / g.MaxScore), 1);
        var recent = await visible.OrderByDescending(a => a.OpensAt).Take(20).ToListAsync(ct);
        return new OverviewView(total, open, waiting, recentGraded.Count, average, byKind.OrderBy(k => k.Kind).ToList(), await ViewsAsync(recent, ct));
    }

    private async Task<AttemptReview> ReviewViewAsync(Attempt attempt, Assignment assignment, CancellationToken ct)
    {
        var items = await db.AttemptItems.AsNoTracking().Where(i => i.AttemptId == attempt.Id).OrderBy(i => i.Ordinal).ToListAsync(ct);
        var events = await db.AttemptEvents.AsNoTracking().Where(e => e.AttemptId == attempt.Id).OrderBy(e => e.OccurredAt)
            .Select(e => new AttemptEventView(e.Kind, e.ItemId, e.Value, e.OccurredAt)).ToListAsync(ct);
        var submitted = attempt.Status != AttemptStatus.InProgress;
        return new AttemptReview(attempt.Id, assignment.Id, assignment.Title, assignment.ClassId, attempt.StudentId, await NameAsync(attempt.StudentId, ct),
            attempt.Number, attempt.Status, attempt.StartedAt, attempt.SubmittedAt, attempt.Late, attempt.AutoSubmitted, attempt.Score, attempt.RawScore,
            attempt.MaxScore, attempt.PenaltyPercent, attempt.PendingItems, attempt.Comment, assignment.Grader, AttemptViews.Skills(items),
            attempt.Layout.Sections, items.Select(i => AttemptViews.Result(i, submitted, true)).ToList(), events);
    }

    private async Task<(Assignment? Assignment, Error? Error)> ManagedAsync(Guid id, Actor actor, CancellationToken ct)
    {
        var assignment = await db.Assignments.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (assignment is null)
        {
            return (null, NotFound);
        }

        return await access.CanManageClassAsync(assignment.ClassId, assignment.BranchId, actor, ct) ? (assignment, null) : (null, NotFound);
    }

    private async Task<(Attempt? Attempt, Assignment? Assignment, Error? Error)> ManagedAttemptAsync(Guid attemptId, Actor actor, CancellationToken ct)
    {
        var attempt = await db.Attempts.FirstOrDefaultAsync(t => t.Id == attemptId, ct);
        var assignment = attempt is null ? null : await db.Assignments.FirstOrDefaultAsync(a => a.Id == attempt.AssignmentId, ct);
        if (attempt is null || assignment is null || !await access.CanManageClassAsync(assignment.ClassId, assignment.BranchId, actor, ct))
        {
            return (null, null, Error.NotFound("assessment.attempt_not_found"));
        }

        return (attempt, assignment, null);
    }

    private async Task<IReadOnlyList<AssignmentView>> ViewsAsync(IReadOnlyList<Assignment> rows, CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var ids = rows.Select(a => a.Id).ToArray();
        var paperIds = rows.Select(a => a.PaperId).Distinct().ToArray();
        var papers = await db.Papers.AsNoTracking().Where(p => paperIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Title, ct);
        var stats = await db.Attempts.AsNoTracking().Where(t => ids.Contains(t.AssignmentId))
            .GroupBy(t => t.AssignmentId)
            .Select(g => new
            {
                g.Key,
                Started = g.Select(t => t.StudentId).Distinct().Count(),
                Submitted = g.Where(t => t.Status != AttemptStatus.InProgress).Select(t => t.StudentId).Distinct().Count(),
                Graded = g.Where(t => t.Status == AttemptStatus.Graded).Select(t => t.StudentId).Distinct().Count(),
                Waiting = g.Count(t => t.Status == AttemptStatus.Submitted),
            }).ToListAsync(ct);
        var codes = new Dictionary<Guid, string>();
        var rosterSizes = new Dictionary<Guid, int>();
        var views = new List<AssignmentView>();
        foreach (var a in rows)
        {
            if (!rosterSizes.TryGetValue(a.ClassId, out var size))
            {
                size = (await academic.GetRosterAsync(a.ClassId, ct)).Count;
                rosterSizes[a.ClassId] = size;
            }

            var s = stats.FirstOrDefault(x => x.Key == a.Id);
            views.Add(new AssignmentView(a.Id, a.PaperId, papers.GetValueOrDefault(a.PaperId, string.Empty), a.Title, a.Target, a.ClassId,
                await ClassCodeAsync(a.ClassId, codes, ct), a.BranchId, a.SessionId, a.StudentIds, a.Kind, a.OpensAt, a.DueAt, a.LatePolicy,
                a.LatePenaltyPercent, a.MaxAttempts, a.ScoringRule, a.Grader, a.ScoreRelease, a.ReleaseAt, a.AnswerVisibility, a.TimeLimitMinutes,
                a.CreatedBy, a.CreatedAt,
                new AssignmentStats(a.Target == AssignmentTarget.Students ? a.StudentIds.Count : size, s?.Started ?? 0, s?.Submitted ?? 0, s?.Graded ?? 0,
                    s?.Waiting ?? 0)));
        }

        return views;
    }

    private async Task<string> ClassCodeAsync(Guid classId, Dictionary<Guid, string> cache, CancellationToken ct)
    {
        if (!cache.TryGetValue(classId, out var code))
        {
            code = (await academic.GetClassAsync(classId, ct))?.Code ?? string.Empty;
            cache[classId] = code;
        }

        return code;
    }

    private async Task<string> NameAsync(Guid studentId, CancellationToken ct) => (await crm.GetStudentAsync(studentId, ct))?.FullName ?? string.Empty;

    private static AssignmentSettings Settings(AssignmentKind kind, DateTimeOffset opensAt, DateTimeOffset? dueAt, LatePolicy late, int penalty, int? max,
        ScoringRule rule, GraderMode grader, ScoreRelease release, DateTimeOffset? releaseAt, AnswerVisibility answers, int? minutes) =>
        new(kind, opensAt, dueAt, late, penalty, max, rule, grader, release, releaseAt, answers, minutes);
}
