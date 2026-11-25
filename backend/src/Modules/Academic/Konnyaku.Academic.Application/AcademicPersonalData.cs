using Konnyaku.Application.Privacy;
using Microsoft.EntityFrameworkCore;

namespace Konnyaku.Academic.Application;

/// <summary>A student's enrollments (class, dates, status) for the data subject export.</summary>
public sealed class AcademicPersonalData(IAcademicDbContext db) : IPersonalDataSource
{
    public string Section => "academic";

    public async Task<object?> ExportAsync(PersonalDataSubject subject, CancellationToken cancellationToken) =>
        await (from e in db.Enrollments.AsNoTracking()
               join c in db.Classes on e.ClassId equals c.Id
               where e.StudentId == subject.StudentId
               orderby e.CreatedAt
               select new
               {
                   enrollmentId = e.Id,
                   classCode = c.Code,
                   type = e.Type.ToString(),
                   status = e.Status.ToString(),
                   e.StartsOn,
                   e.FrozenFrom,
                   e.FrozenTo,
                   e.CreatedAt,
               }).ToListAsync(cancellationToken);
}
