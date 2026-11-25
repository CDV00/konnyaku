using System.Security.Claims;
using Konnyaku.Application.Authorization;
using Konnyaku.Identity.Application;
using Konnyaku.Identity.Domain;
using Konnyaku.Infrastructure.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Konnyaku.Identity.Infrastructure;

/// <summary>
/// Permissions of the signed-in staff member from their active membership's role assignments, loaded once per request.
/// A role assigned for a branch counts for that branch only; branch groups are not used yet and grant nothing.
/// </summary>
internal sealed class AccessControl(IIdentityDbContext db, IHttpContextAccessor http) : IAccessControl
{
    private IReadOnlyList<RoleGrant>? _grants;

    public async Task<bool> HasAsync(string permission, CancellationToken cancellationToken) =>
        (await GrantsAsync(cancellationToken)).Any(g => RolePermissions.Grants(g.Role, permission));

    public async Task<bool> CanAsync(string permission, Guid? branchId, CancellationToken cancellationToken) =>
        AccessRules.Allows(await GrantsAsync(cancellationToken), permission, branchId);

    public async Task<IReadOnlyList<PermissionScope>> ListAsync(CancellationToken cancellationToken) =>
        AccessRules.Scopes((await GrantsAsync(cancellationToken)).ToList());

    private async Task<IReadOnlyList<RoleGrant>> GrantsAsync(CancellationToken cancellationToken)
    {
        if (_grants is not null)
        {
            return _grants;
        }

        var user = http.HttpContext?.User;
        if (user?.FindFirstValue(KonnyakuClaims.Audience) != Audiences.Staff || user.UserId() is not { } userId)
        {
            return _grants = [];
        }

        var assignments = await db.Memberships.AsNoTracking()
            .Where(m => m.UserId == userId && m.Status == MembershipStatus.Active)
            .SelectMany(m => m.Roles)
            .Where(r => r.ScopeType == ScopeType.Tenant || (r.ScopeType == ScopeType.Branch && r.ScopeId != null))
            .Select(r => new { r.Role, r.ScopeType, r.ScopeId })
            .ToListAsync(cancellationToken);
        return _grants = assignments
            .Where(r => Roles.Staff.Contains(r.Role))
            .Select(r => new RoleGrant(r.Role, r.ScopeType == ScopeType.Branch ? r.ScopeId : null))
            .ToList();
    }
}
