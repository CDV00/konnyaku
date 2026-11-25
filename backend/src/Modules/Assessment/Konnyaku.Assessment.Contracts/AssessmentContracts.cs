using Konnyaku.SharedKernel;

namespace Konnyaku.Assessment.Contracts;

/// <summary>Score of one skill within an attempt (Catalog skill code; empty when the questions name none).</summary>
public sealed record SkillScoreDto(string SkillCode, decimal Score, decimal MaxScore);

/// <summary>
/// An attempt got its official score (all items graded; re-sent with the new score when a teacher regrades). The learner
/// sees it from <see cref="VisibleFrom"/> (release policy of the assignment). For the gradebook and notifications.
/// </summary>
public sealed record AssessmentGradedV1(
    Guid AttemptId, Guid AssignmentId, Guid PaperId, Guid ClassId, Guid StudentId, string Kind, int AttemptNumber, decimal Score,
    decimal MaxScore, bool Late, DateTimeOffset GradedAt, DateTimeOffset VisibleFrom, IReadOnlyList<SkillScoreDto> Skills) : IIntegrationEvent;
