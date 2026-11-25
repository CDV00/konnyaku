using Konnyaku.SharedKernel;

namespace Konnyaku.Scheduling.Contracts;

public sealed record SessionDto(
    Guid Id, Guid ClassId, Guid BranchId, Guid? LessonId, int SequenceNo, DateTimeOffset StartsAt, DateTimeOffset EndsAt,
    DateOnly LocalDate, string TimeZone, string DeliveryMode, Guid? TeacherId, Guid? RoomId, string Status);

public interface ISchedulingApi
{
    Task<SessionDto?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken);

    /// <summary>Sessions of a class that are not cancelled (0 = the class is not scheduled).</summary>
    Task<int> CountClassSessionsAsync(Guid classId, CancellationToken cancellationToken);
}

public sealed record SessionScheduledV1(
    Guid SessionId, Guid ClassId, Guid BranchId, DateTimeOffset StartsAt, DateTimeOffset EndsAt, Guid? TeacherId, string DeliveryMode)
    : IIntegrationEvent;

public sealed record SessionRescheduledV1(Guid SessionId, DateTimeOffset StartsAt, DateTimeOffset EndsAt) : IIntegrationEvent;

public sealed record SessionCancelledV1(Guid SessionId, string Reason) : IIntegrationEvent;
