using Konnyaku.Application.Privacy;
using Microsoft.EntityFrameworkCore;

namespace Konnyaku.Learning.Application;

/// <summary>A student's attendance (session, date, status, time present) for the data subject export.</summary>
public sealed class LearningPersonalData(ILearningDbContext db) : IPersonalDataSource
{
    public string Section => "learning";

    public async Task<object?> ExportAsync(PersonalDataSubject subject, CancellationToken cancellationToken) =>
        await db.Attendances.AsNoTracking().Where(a => a.StudentId == subject.StudentId).OrderBy(a => a.LocalDate)
            .Select(a => new
            {
                a.SessionId, a.ClassId, a.LocalDate, status = a.Status.ToString(), source = a.Source.ToString(), a.JoinedAt, a.LeftAt,
                a.PresentSeconds, a.Billable, a.ConfirmedAt,
            })
            .ToListAsync(cancellationToken);
}
