using Konnyaku.SharedKernel;

namespace Konnyaku.Identity.Application;

/// <summary>
/// Filters of the staff list (<c>GET /api/identity/staff</c>): <see cref="Q"/> searches name and email without accents,
/// <see cref="Role"/> a staff role, <see cref="Status"/> <c>Active</c> or <c>Suspended</c>, <see cref="Branch"/> members
/// holding a role for that branch (or center-wide).
/// </summary>
public sealed record StaffFilter(string? Q = null, string? Role = null, string? Status = null, Guid? Branch = null);

/// <summary>
/// Staff of one center are tens to a few hundred people: the list is read whole (as before) and filtered, sorted and
/// paged in memory, returned in the same page shape as the other lists.
/// </summary>
public sealed class StaffList(StaffQueries queries)
{
    public static readonly IReadOnlySet<string> Sorts = new HashSet<string> { "name", "email", "status" };

    public async Task<Page<StaffMemberView>> ListAsync(StaffFilter filter, PageRequest page, CancellationToken ct)
    {
        IEnumerable<StaffMemberView> staff = await queries.ListAsync(ct);
        if (TextSearch.Term(filter.Q) is { } term)
        {
            staff = staff.Where(s => TextSearch.Fold($"{s.DisplayName} {s.Email}").Contains(term, StringComparison.Ordinal));
        }

        if (!string.IsNullOrWhiteSpace(filter.Role))
        {
            staff = staff.Where(s => s.Roles.Contains(filter.Role));
        }

        if (!string.IsNullOrWhiteSpace(filter.Status))
        {
            staff = staff.Where(s => string.Equals(s.Status, filter.Status, StringComparison.OrdinalIgnoreCase));
        }

        if (filter.Branch is { } branch)
        {
            staff = staff.Where(s => s.Grants.Any(g => g.BranchId is null || g.BranchId == branch));
        }

        staff = (page.Sort, page.Descending) switch
        {
            ("email", var desc) => desc ? staff.OrderByDescending(s => s.Email) : staff.OrderBy(s => s.Email),
            ("status", var desc) => desc
                ? staff.OrderByDescending(s => s.Status).ThenBy(s => s.DisplayName, StringComparer.CurrentCulture)
                : staff.OrderBy(s => s.Status).ThenBy(s => s.DisplayName, StringComparer.CurrentCulture),
            ("name", true) => staff.OrderByDescending(s => s.DisplayName, StringComparer.CurrentCulture),
            ("name", false) => staff.OrderBy(s => s.DisplayName, StringComparer.CurrentCulture),
            _ => staff, // active first, then by name (the query's order)
        };
        return Page<StaffMemberView>.FromList(staff.ToList(), page);
    }
}
