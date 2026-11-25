using Konnyaku.SharedKernel;

namespace Konnyaku.Academic.Contracts;

public sealed record LessonDto(Guid Id, int Ordinal, IReadOnlyDictionary<string, string> Title);

public sealed record ClassDto(
    Guid Id, string Code, Guid CourseLevelId, Guid BranchId, string DeliveryMode, int Capacity, int EnrolledCount,
    DateOnly StartsOn, string TimeZone, string Status, int SessionMinutes, string LanguageCode);

public sealed record RosterEntryDto(Guid EnrollmentId, Guid StudentId, string Status);

public interface IAcademicApi
{
    Task<ClassDto?> GetClassAsync(Guid classId, CancellationToken cancellationToken);

    Task<IReadOnlyList<LessonDto>> GetLessonsAsync(Guid classId, CancellationToken cancellationToken);

    /// <summary>Enrollments holding a seat (active or frozen) — who may join the class's sessions.</summary>
    Task<IReadOnlyList<RosterEntryDto>> GetRosterAsync(Guid classId, CancellationToken cancellationToken);

    /// <summary>Classes in which the students hold a seat (active or frozen enrollment).</summary>
    Task<IReadOnlyList<Guid>> ListClassIdsOfStudentsAsync(IReadOnlyCollection<Guid> studentIds, CancellationToken cancellationToken);
}

public sealed record EnrollmentActivatedV1(Guid EnrollmentId, Guid ClassId, Guid StudentId, string Type, Guid? OrderId) : IIntegrationEvent;

/// <summary>A paid order could not be turned into an enrollment (class full or closed): Billing flags it for refund.</summary>
public sealed record EnrollmentRejectedV1(Guid OrderId, Guid ClassId, Guid StudentId, string Reason) : IIntegrationEvent;

public sealed record EnrollmentDroppedV1(Guid EnrollmentId, Guid ClassId, Guid StudentId) : IIntegrationEvent;

/// <summary>A class switched between online, offline and hybrid: Scheduling updates its future sessions (rooms follow).</summary>
public sealed record ClassDeliveryModeChangedV1(Guid ClassId, string DeliveryMode) : IIntegrationEvent;

/// <summary>A class was completed or cancelled (<see cref="Status"/>): Scheduling cancels its remaining planned sessions.</summary>
public sealed record ClassClosedV1(Guid ClassId, string Status) : IIntegrationEvent;
