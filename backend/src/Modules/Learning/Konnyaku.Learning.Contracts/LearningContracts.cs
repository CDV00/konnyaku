using Konnyaku.SharedKernel;

namespace Konnyaku.Learning.Contracts;

/// <summary>Teacher-confirmed attendance. Billing spends one lesson when <see cref="Billable"/>.</summary>
public sealed record AttendanceConfirmedV1(
    Guid AttendanceId, Guid SessionId, Guid EnrollmentId, Guid StudentId, Guid BranchId, DateOnly LocalDate, string Status, bool Billable)
    : IIntegrationEvent;

/// <summary>Who may watch a recording, decided by the recording access policy. Workspace applies it.</summary>
public sealed record RecordingAccessGrantedV1(Guid SessionId, Guid RecordingId, IReadOnlyList<Guid> ViewerUserIds) : IIntegrationEvent;
