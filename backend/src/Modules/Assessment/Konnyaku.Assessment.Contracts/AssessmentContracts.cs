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

/// <summary>A new assignment was given (learners may start it from <see cref="OpensAt"/>). For notifications.</summary>
public sealed record AssignmentPublishedV1(Guid AssignmentId, Guid ClassId, DateTimeOffset OpensAt, DateTimeOffset? DueAt) : IIntegrationEvent;

/// <summary>The opening time or deadline of an assignment changed. For notifications (due-date reminders move).</summary>
public sealed record AssignmentRescheduledV1(Guid AssignmentId, DateTimeOffset OpensAt, DateTimeOffset? DueAt) : IIntegrationEvent;

/// <summary>
/// Who an assignment is for: <see cref="StudentIds"/> (chosen learners, or the active roster of its class) and those of
/// them who already handed it in (<see cref="SubmittedStudentIds"/>).
/// </summary>
public sealed record AssignmentAudienceDto(
    Guid AssignmentId, Guid ClassId, string Title, string Kind, DateTimeOffset OpensAt, DateTimeOffset? DueAt, IReadOnlyList<Guid> StudentIds,
    IReadOnlyList<Guid> SubmittedStudentIds);

public interface IAssessmentApi
{
    /// <summary>Null when the assignment does not exist (deleted) in the current tenant.</summary>
    Task<AssignmentAudienceDto?> GetAssignmentAudienceAsync(Guid assignmentId, CancellationToken cancellationToken);
}
