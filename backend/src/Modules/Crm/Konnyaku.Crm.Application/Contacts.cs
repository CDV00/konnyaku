using Konnyaku.Crm.Contracts;
using Konnyaku.Crm.Domain;
using Microsoft.EntityFrameworkCore;

namespace Konnyaku.Crm.Application;

public sealed class CrmContacts(ICrmDbContext db) : ICrmContactsApi
{
    public async Task<StudentContactDto?> GetStudentContactAsync(Guid studentId, CancellationToken cancellationToken)
    {
        var student = await db.Students.AsNoTracking().FirstOrDefaultAsync(s => s.Id == studentId, cancellationToken);
        return student is null ? null : ToContact(student);
    }

    public async Task<IReadOnlyList<StudentContactDto>> ListStudentContactsOfUserAsync(Guid userId, CancellationToken cancellationToken) =>
        (await db.Students.AsNoTracking().Where(s => s.UserId == userId).ToListAsync(cancellationToken)).Select(ToContact).ToList();

    public async Task<LeadSummaryDto?> GetLeadSummaryAsync(Guid leadId, CancellationToken cancellationToken) =>
        await db.Leads.AsNoTracking().Where(l => l.Id == leadId)
            .Select(l => new LeadSummaryDto(l.Id, l.StudentName, l.LanguageCode, l.Locale))
            .FirstOrDefaultAsync(cancellationToken);

    private static StudentContactDto ToContact(StudentProfile s) => new(s.Id, s.FullName, s.Email, s.UserId, s.CommunicationLocale);
}
