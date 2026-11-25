using Konnyaku.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Konnyaku.Application.Persistence;

public static class PagingExtensions
{
    /// <summary>Counts the filtered rows, then reads one page of the already ordered query.</summary>
    public static async Task<Page<T>> ToPageAsync<T>(this IQueryable<T> ordered, PageRequest request, CancellationToken cancellationToken)
    {
        var total = await ordered.CountAsync(cancellationToken);
        if (total == 0 || request.Skip >= total)
        {
            return new Page<T>([], total, request.Page, request.PageSize);
        }

        var items = await ordered.Skip(request.Skip).Take(request.PageSize).ToListAsync(cancellationToken);
        return new Page<T>(items, total, request.Page, request.PageSize);
    }
}
