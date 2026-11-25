using Konnyaku.Academic.Contracts;
using Konnyaku.Academic.Domain;
using Microsoft.EntityFrameworkCore;

namespace Konnyaku.Academic.Application;

public sealed class AcademicUsageQueries(IAcademicDbContext db) : IAcademicUsageApi
{
    public Task<int> CountActiveClassesAsync(CancellationToken cancellationToken) =>
        db.Classes.CountAsync(c => c.Status != ClassStatus.Completed && c.Status != ClassStatus.Cancelled, cancellationToken);
}
