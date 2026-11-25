using System.Security.Cryptography;
using Konnyaku.Application.Tenancy;
using Konnyaku.Identity.Contracts;
using Konnyaku.Identity.Domain;
using Konnyaku.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Konnyaku.Identity.Application;

public sealed class IdentityPlatformApi(IIdentityDbContext db, ITenantContext tenant, BootstrapTenantAdminHandler bootstrap) : IIdentityPlatformApi
{
    private const string PasswordAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";

    public async Task<Result<TenantAdminProvisionedDto>> ProvisionTenantAdminAsync(string email, string displayName, CancellationToken cancellationToken)
    {
        var normalized = User.NormalizeEmail(email ?? string.Empty);
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return Error.Validation("identity.display_name_required");
        }

        var existing = await db.Users.Where(u => u.Email == normalized).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(cancellationToken);
        var hasPassword = existing is { } id && await db.PasswordCredentials.AnyAsync(c => c.UserId == id, cancellationToken);
        // The bootstrap handler only sets a password for a person who has none, so an existing password is kept.
        var temporaryPassword = hasPassword ? null : RandomNumberGenerator.GetString(PasswordAlphabet, 14);
        var result = await bootstrap.HandleAsync(
            new BootstrapTenantAdminCommand(tenant.RequiredTenantId, normalized, displayName, temporaryPassword ?? RandomNumberGenerator.GetString(PasswordAlphabet, 14)),
            cancellationToken);
        return result.IsSuccess
            ? new TenantAdminProvisionedDto(result.Value, normalized, temporaryPassword)
            : result.Error!;
    }

    public async Task<int> CountStaffAsync(CancellationToken cancellationToken)
    {
        var staffRoles = Roles.Staff.ToList();
        return await db.Memberships.CountAsync(
            m => m.Status == MembershipStatus.Active && m.Roles.Any(r => staffRoles.Contains(r.Role)), cancellationToken);
    }
}
