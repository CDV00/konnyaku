using Konnyaku.Application.Persistence;
using Konnyaku.Assessment.Contracts;
using Konnyaku.Assessment.Domain;
using Microsoft.EntityFrameworkCore;

namespace Konnyaku.Assessment.Application;

/// <summary>Builds attempts from papers, hands in expired ones and announces official scores.</summary>
public sealed class AttemptService(IAssessmentDbContext db, TimeProvider clock)
{
    /// <summary>
    /// The items of a new attempt: fixed questions, whole reading groups (kept together) and random draws, with a frozen
    /// snapshot of each question version and options shuffled for this learner.
    /// </summary>
    public async Task<(AttemptLayout Layout, List<(int Section, Question Question, QuestionGroup? Group, SectionItem Item)> Items)> DrawAsync(
        Paper paper, CancellationToken ct)
    {
        var layout = new AttemptLayout();
        var drawn = new List<(int, Question, QuestionGroup?, SectionItem)>();
        var used = new HashSet<Guid>();
        foreach (var section in paper.Layout.Sections)
        {
            var blocks = new List<List<(Question, QuestionGroup?, SectionItem)>>();
            foreach (var item in section.Items)
            {
                switch (item.Kind)
                {
                    case SectionItemKind.Question:
                        var question = await db.Questions.AsNoTracking().FirstOrDefaultAsync(q => q.Id == item.QuestionId && !q.Archived, ct);
                        if (question is not null && used.Add(question.Id))
                        {
                            blocks.Add([(question, null, item)]);
                        }

                        break;
                    case SectionItemKind.Group:
                        var group = await db.Groups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == item.GroupId, ct);
                        if (group is null)
                        {
                            break;
                        }

                        var members = await db.Questions.AsNoTracking().Where(q => q.GroupId == group.Id && !q.Archived).OrderBy(q => q.Id).ToListAsync(ct);
                        var block = members.Where(q => used.Add(q.Id)).Select(q => (q, (QuestionGroup?)group, item)).ToList();
                        if (block.Count > 0)
                        {
                            blocks.Add(block);
                        }

                        break;
                    case SectionItemKind.Random:
                        var exclude = used.ToArray();
                        var picks = await QuestionPool.For(db.Questions.AsNoTracking(), item).Where(q => !exclude.Contains(q.Id))
                            .OrderBy(_ => EF.Functions.Random()).Take(item.Count).ToListAsync(ct);
                        foreach (var pick in picks.Where(p => used.Add(p.Id)))
                        {
                            blocks.Add([(pick, null, item)]);
                        }

                        break;
                }
            }

            if (blocks.Count == 0)
            {
                continue;
            }

            if (section.ShuffleQuestions)
            {
                Random.Shared.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(blocks));
            }

            var index = layout.Sections.Count;
            layout.Sections.Add(new AttemptSection { Title = section.Title, SkillCode = section.SkillCode, TimeLimitMinutes = section.TimeLimitMinutes });
            drawn.AddRange(blocks.SelectMany(b => b).Select(x => (index, x.Item1, x.Item2, x.Item3)));
        }

        return (layout, drawn);
    }

    public static ItemSnapshot Snapshot(Paper paper, AttemptSection section, Question q, QuestionGroup? group, SectionItem item) => new()
    {
        Type = q.Type,
        Version = q.Version,
        Stem = q.Stem,
        Content = Shuffled(q.Type, q.Content, q.Key),
        Key = q.Key,
        Points = item.Points ?? q.Points,
        Explanation = q.Explanation,
        GroupId = group?.Id,
        GroupTitle = group?.Title,
        Passage = group?.Passage,
        SkillCode = section.SkillCode ?? q.SkillCode,
        LevelCode = q.LevelCode,
        Rubric = item.RubricId is null ? null : paper.Layout.Rubrics.FirstOrDefault(r => r.Id == item.RubricId),
    };

    private static QuestionContent Shuffled(QuestionType type, QuestionContent content, AnswerKey key)
    {
        var copy = new QuestionContent
        {
            Options = content.Options.Select(o => new ChoiceOption { Id = o.Id, Html = o.Html }).ToList(),
            Targets = content.Targets.Select(o => new ChoiceOption { Id = o.Id, Html = o.Html }).ToList(),
            Blanks = content.Blanks,
            Essay = content.Essay,
            Shuffle = content.Shuffle,
        };
        if (type == QuestionType.Ordering)
        {
            // Never hand out the answer: reshuffle while the order is the correct one.
            for (var i = 0; i < 5 && copy.Options.Select(o => o.Id).SequenceEqual(key.Order); i++)
            {
                Random.Shared.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(copy.Options));
            }
        }
        else if (content.Shuffle && type is QuestionType.SingleChoice or QuestionType.MultipleChoice or QuestionType.Matching)
        {
            Random.Shared.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(copy.Options));
            Random.Shared.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(copy.Targets));
        }

        return copy;
    }

    /// <summary>Hands in an attempt whose time is up, as of its deadline. False when it was not expired (or another writer won).</summary>
    public async Task<bool> SubmitIfExpiredAsync(Attempt attempt, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (!attempt.Expired(now))
        {
            return false;
        }

        var assignment = await db.Assignments.FirstAsync(a => a.Id == attempt.AssignmentId, ct);
        var items = await db.AttemptItems.Where(i => i.AttemptId == attempt.Id).OrderBy(i => i.Ordinal).ToListAsync(ct);
        attempt.Submit(assignment, items, now, auto: true);
        AnnounceIfGraded(attempt, assignment, items);
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            // The learner submitted (or saved) at the same moment; their request decides.
            db.Reset();
            return false;
        }
    }

    /// <summary>Publishes <see cref="AssessmentGradedV1"/> when the attempt holds an official score.</summary>
    public void AnnounceIfGraded(Attempt attempt, Assignment assignment, IReadOnlyList<AttemptItem> items)
    {
        if (attempt.Status != AttemptStatus.Graded || attempt.Score is not { } score || attempt.GradedAt is not { } gradedAt)
        {
            return;
        }

        var skills = items.GroupBy(i => i.Snapshot.SkillCode ?? string.Empty)
            .Select(g => new SkillScoreDto(g.Key, g.Sum(i => i.Score ?? 0), g.Sum(i => i.Snapshot.Points)))
            .OrderBy(s => s.SkillCode, StringComparer.Ordinal).ToList();
        db.Publish(new AssessmentGradedV1(attempt.Id, assignment.Id, assignment.PaperId, assignment.ClassId, attempt.StudentId, assignment.Kind.ToString(),
            attempt.Number, score, attempt.MaxScore, attempt.Late, gradedAt, ReleasePolicy.VisibleFrom(assignment, gradedAt), skills));
    }
}

/// <summary>Worker job body: hands in the expired attempts of the current tenant.</summary>
public sealed class ExpiredAttempts(IAssessmentDbContext db, AttemptService attempts, TimeProvider clock)
{
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var expired = await db.Attempts.Where(a => a.Status == AttemptStatus.InProgress && a.DeadlineAt != null && a.DeadlineAt <= now)
            .OrderBy(a => a.DeadlineAt).Take(200).ToListAsync(ct);
        var count = 0;
        foreach (var attempt in expired)
        {
            if (await attempts.SubmitIfExpiredAsync(attempt, ct))
            {
                count++;
            }
        }

        return count;
    }
}

internal static class SaveExtensions
{
    /// <summary>Saves; a unique violation (two tabs starting the same attempt) becomes false instead of an exception.</summary>
    public static async Task<bool> TrySaveAsync(this IAssessmentDbContext db, IDbErrorClassifier errors, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (errors.IsUniqueViolation(ex))
        {
            return false;
        }
    }
}
