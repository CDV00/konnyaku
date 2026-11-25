using Konnyaku.SharedKernel;

namespace Konnyaku.Crm.Contracts;

public sealed record StudentDto(
    Guid Id, string FullName, int? BirthYear, Guid? HomeBranchId, Guid? UserId, string CommunicationLocale, bool IsActive);

public interface ICrmApi
{
    Task<StudentDto?> GetStudentAsync(Guid studentId, CancellationToken cancellationToken);

    /// <summary>Student profiles linked to a signed-in learner account.</summary>
    Task<IReadOnlyList<StudentDto>> ListStudentsOfUserAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// Ids of the students whose name, phone or email contains <paramref name="query"/> (accents ignored), at most
    /// <paramref name="limit"/>: lets another module filter its own lists by student ("orders of Ân").
    /// </summary>
    Task<IReadOnlyList<Guid>> SearchStudentIdsAsync(string query, int limit, CancellationToken cancellationToken);
}

public sealed record LeadCreatedV1(Guid LeadId, string LanguageCode, Guid? AssignedConsultantId, DateTimeOffset? FollowUpDueAt, string Locale) : IIntegrationEvent;

public sealed record StudentRegisteredV1(Guid StudentId, Guid? LeadId, Guid? HomeBranchId) : IIntegrationEvent;

/// <summary>
/// The personal data of a student was erased on request. Modules holding a copy of it (the learner account's name and
/// email, names seen in online meetings) erase theirs; records kept by law (ledger, orders, attendance) stay as they are.
/// </summary>
public sealed record StudentAnonymizedV1(Guid StudentId, Guid? UserId) : IIntegrationEvent;
