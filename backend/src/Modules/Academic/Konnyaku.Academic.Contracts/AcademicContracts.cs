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

public sealed record EnrollmentActivatedV1(Guid EnrollmentId, Guid ClassId, Guid StudentId, string Type) : IIntegrationEvent;

public sealed record EnrollmentDroppedV1(Guid EnrollmentId, Guid ClassId, Guid StudentId) : IIntegrationEvent;
