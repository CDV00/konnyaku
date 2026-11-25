using Konnyaku.Assessment.Contracts;
using Konnyaku.Assessment.Domain;

namespace Konnyaku.Assessment.Application;

/// <summary>One question as the learner sees it while working (no key). Locked items of later timed sections carry no content.</summary>
public sealed record PlayItem(
    Guid Id, int Ordinal, int SectionIndex, bool Locked, QuestionType Type, string Stem, IReadOnlyList<ChoiceOption> Options,
    IReadOnlyList<ChoiceOption> Targets, int Blanks, EssayLimits? Essay, decimal Points, Guid? GroupId, string? GroupTitle, string? Passage,
    ItemResponse Response, bool Flagged);

public sealed record AttemptPlayView(
    Guid Id, Guid AssignmentId, string Title, AssignmentKind Kind, int Number, AttemptStatus Status, DateTimeOffset StartedAt,
    DateTimeOffset? DeadlineAt, int SectionIndex, DateTimeOffset? SectionDeadlineAt, IReadOnlyList<AttemptSection> Sections, bool Timed,
    bool ShowRuby, DateTimeOffset ServerNow, IReadOnlyList<PlayItem> Items);

/// <summary>
/// One graded question. Score and correctness only when scores are released; the key and explanation only when answers
/// are (always for staff).
/// </summary>
public sealed record ResultItem(
    Guid Id, int Ordinal, int SectionIndex, QuestionType Type, string Stem, IReadOnlyList<ChoiceOption> Options, IReadOnlyList<ChoiceOption> Targets,
    int Blanks, EssayLimits? Essay, decimal Points, string? GroupTitle, string? Passage, string? SkillCode, ItemResponse Response, bool Flagged,
    ItemGradeState? State, decimal? Score, bool? Correct, AnswerKey? Key, string? Explanation, string? Comment, Rubric? Rubric,
    IReadOnlyDictionary<string, decimal>? Bands, int QuestionVersion);

public sealed record AttemptResultView(
    Guid Id, Guid AssignmentId, string Title, AssignmentKind Kind, int Number, AttemptStatus Status, DateTimeOffset StartedAt,
    DateTimeOffset? SubmittedAt, bool Late, bool AutoSubmitted, bool ScoresVisible, bool AnswersVisible, DateTimeOffset? ReleaseAt,
    decimal? Score, decimal? RawScore, decimal MaxScore, int PenaltyPercent, int PendingItems, string? Comment, IReadOnlyList<SkillScoreDto> Skills,
    bool ShowRuby, IReadOnlyList<AttemptSection> Sections, IReadOnlyList<ResultItem> Items);

public sealed record AttemptEventView(AttemptEventKind Kind, Guid? ItemId, int Value, DateTimeOffset OccurredAt);

/// <summary>The teacher's view of an attempt: everything, plus the tab and paste log.</summary>
public sealed record AttemptReview(
    Guid Id, Guid AssignmentId, string AssignmentTitle, Guid ClassId, Guid StudentId, string StudentName, int Number, AttemptStatus Status,
    DateTimeOffset StartedAt, DateTimeOffset? SubmittedAt, bool Late, bool AutoSubmitted, decimal? Score, decimal RawScore, decimal MaxScore,
    int PenaltyPercent, int PendingItems, string? Comment, GraderMode Grader, IReadOnlyList<SkillScoreDto> Skills,
    IReadOnlyList<AttemptSection> Sections, IReadOnlyList<ResultItem> Items, IReadOnlyList<AttemptEventView> Events);

internal static class AttemptViews
{
    public static PlayItem Play(AttemptItem i, Attempt attempt)
    {
        var s = i.Snapshot;
        var locked = attempt.Timed && i.SectionIndex > attempt.SectionIndex;
        return locked
            ? new PlayItem(i.Id, i.Ordinal, i.SectionIndex, true, s.Type, string.Empty, [], [], 0, null, s.Points, s.GroupId, null, null, new ItemResponse(), false)
            : new PlayItem(i.Id, i.Ordinal, i.SectionIndex, false, s.Type, s.Stem, OptionsOf(s), s.Content.Targets, s.Content.Blanks, s.Content.Essay,
                s.Points, s.GroupId, s.GroupTitle, s.Passage, i.Response, i.Flagged);
    }

    public static ResultItem Result(AttemptItem i, bool scores, bool answers)
    {
        var s = i.Snapshot;
        bool? correct = scores && i.State is ItemGradeState.Auto or ItemGradeState.Teacher && i.Score is { } score ? score >= s.Points : null;
        return new ResultItem(i.Id, i.Ordinal, i.SectionIndex, s.Type, s.Stem, OptionsOf(s), s.Content.Targets, s.Content.Blanks, s.Content.Essay, s.Points,
            s.GroupTitle, s.Passage, s.SkillCode, i.Response, i.Flagged, scores ? i.State : null, scores ? i.Score : null, correct,
            answers ? s.Key : null, answers ? s.Explanation : null, scores ? i.Comment : null, scores ? s.Rubric : null,
            scores && i.Bands.Bands.Count > 0 ? i.Bands.Bands : null, i.QuestionVersion);
    }

    public static IReadOnlyList<SkillScoreDto> Skills(IReadOnlyList<AttemptItem> items) =>
        items.GroupBy(i => i.Snapshot.SkillCode ?? string.Empty)
            .Select(g => new SkillScoreDto(g.Key, g.Sum(i => i.Score ?? 0), g.Sum(i => i.Snapshot.Points)))
            .OrderBy(s => s.SkillCode, StringComparer.Ordinal).ToList();

    /// <summary>True/false options carry no text: the UI labels them.</summary>
    private static IReadOnlyList<ChoiceOption> OptionsOf(ItemSnapshot s) => s.Content.Options;
}
