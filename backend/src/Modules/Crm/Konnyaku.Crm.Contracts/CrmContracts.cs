using Konnyaku.SharedKernel;

namespace Konnyaku.Crm.Contracts;

public sealed record StudentDto(
    Guid Id, string FullName, int? BirthYear, Guid? HomeBranchId, Guid? UserId, string CommunicationLocale);

public interface ICrmApi
{
    Task<StudentDto?> GetStudentAsync(Guid studentId, CancellationToken cancellationToken);
}

public sealed record LeadCreatedV1(Guid LeadId, string LanguageCode, Guid? AssignedConsultantId, DateTimeOffset? FollowUpDueAt, string Locale) : IIntegrationEvent;

public sealed record StudentRegisteredV1(Guid StudentId, Guid? LeadId, Guid? HomeBranchId) : IIntegrationEvent;
