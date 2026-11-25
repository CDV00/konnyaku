namespace Konnyaku.Crm.Contracts;

/// <summary>Where to reach a student (Notification). Kept apart from <see cref="StudentDto"/> so profile readers get no contact data.</summary>
public sealed record StudentContactDto(Guid StudentId, string FullName, string? Email, Guid? UserId, string CommunicationLocale);

public sealed record LeadSummaryDto(Guid LeadId, string StudentName, string LanguageCode, string Locale);

/// <summary>Contact details for notifications. Read-only; used by Notification only.</summary>
public interface ICrmContactsApi
{
    Task<StudentContactDto?> GetStudentContactAsync(Guid studentId, CancellationToken cancellationToken);

    /// <summary>Student profiles linked to a learner account.</summary>
    Task<IReadOnlyList<StudentContactDto>> ListStudentContactsOfUserAsync(Guid userId, CancellationToken cancellationToken);

    Task<LeadSummaryDto?> GetLeadSummaryAsync(Guid leadId, CancellationToken cancellationToken);
}
