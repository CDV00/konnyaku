using Konnyaku.SharedKernel;

namespace Konnyaku.Application.Authorization;

/// <summary>
/// Permissions of the signed-in staff member, read live from their role assignments (a role removed a minute ago no
/// longer counts, whatever the access token says). Implemented by the Identity module.
/// </summary>
public interface IAccessControl
{
    /// <summary>The member holds <paramref name="permission"/> for at least one branch or the whole center.</summary>
    Task<bool> HasAsync(string permission, CancellationToken cancellationToken);

    /// <summary>The permission applies to a record of <paramref name="branchId"/> (null: a center-wide record).</summary>
    Task<bool> CanAsync(string permission, Guid? branchId, CancellationToken cancellationToken);

    Task<IReadOnlyList<PermissionScope>> ListAsync(CancellationToken cancellationToken);
}

public static class AccessControlExtensions
{
    public static readonly Error BranchForbidden = Error.Forbidden("access.branch_forbidden");

    /// <summary>Null when allowed, otherwise the 403 error to return. Every branch must be allowed.</summary>
    public static async Task<Error?> EnsureAsync(this IAccessControl access, string permission, CancellationToken cancellationToken,
        params Guid?[] branchIds)
    {
        foreach (var branchId in branchIds.Distinct())
        {
            if (!await access.CanAsync(permission, branchId, cancellationToken))
            {
                return BranchForbidden;
            }
        }

        return null;
    }
}
