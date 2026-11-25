using Konnyaku.Crm.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Konnyaku.Crm.Application;

public sealed class CrmUsageQueries(ICrmDbContext db) : ICrmUsageApi
{
    public Task<int> CountStudentsAsync(CancellationToken cancellationToken) => db.Students.CountAsync(cancellationToken);
}
