using Konnyaku.SharedKernel;

namespace Konnyaku.Identity.Contracts;

/// <param name="TemporaryPassword">Shown once; null when the person already had a staff password (kept unchanged).</param>
public sealed record TenantAdminProvisionedDto(Guid UserId, string Email, string? TemporaryPassword);

/// <summary>Operations of the platform console on the current tenant (the caller sets the tenant scope).</summary>
public interface IIdentityPlatformApi
{
    /// <summary>Grants TenantAdmin to the person with this email, creating the account with a temporary password if needed.</summary>
    Task<Result<TenantAdminProvisionedDto>> ProvisionTenantAdminAsync(string email, string displayName, CancellationToken cancellationToken);

    /// <summary>Active members holding at least one staff role.</summary>
    Task<int> CountStaffAsync(CancellationToken cancellationToken);
}
