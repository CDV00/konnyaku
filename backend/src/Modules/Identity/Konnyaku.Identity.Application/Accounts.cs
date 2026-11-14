using System.Security.Cryptography;
using Konnyaku.Application.Tenancy;
using Konnyaku.Identity.Contracts;
using Konnyaku.Identity.Domain;
using Konnyaku.SharedKernel;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Konnyaku.Identity.Application;

public interface IIdentityDbContext
{
    DbSet<User> Users { get; }
    DbSet<PasswordCredential> PasswordCredentials { get; }
    DbSet<LocalCredential> LocalCredentials { get; }
    DbSet<Membership> Memberships { get; }

    void Publish(IIntegrationEvent integrationEvent, string? correlationId = null);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

public sealed record SignedIn(Guid UserId, Guid TenantId, string DisplayName, IReadOnlyList<string> Roles, bool MustChangePassword);

internal static class IdentityErrors
{
    // One message for unknown user, wrong password and wrong realm: never reveal whether an account exists.
    public static readonly Error InvalidCredentials = Error.Validation("identity.invalid_credentials");
    public static readonly Error Locked = Error.Validation("identity.account_locked");
    public static readonly Error TenantUnknown = Error.Validation("identity.tenant_unknown");
}

public sealed record StaffLoginCommand(string Email, string Password);

public sealed class StaffLoginHandler(IIdentityDbContext db, ITenantContext tenant, IPasswordHasher<User> hasher, TimeProvider clock)
{
    public async Task<Result<SignedIn>> HandleAsync(StaffLoginCommand command, CancellationToken cancellationToken)
    {
        if (tenant.TenantId is not { } tenantId)
        {
            return IdentityErrors.TenantUnknown;
        }

        var email = User.NormalizeEmail(command.Email ?? string.Empty);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, cancellationToken);
        var credential = user is null ? null : await db.PasswordCredentials.FirstOrDefaultAsync(c => c.UserId == user.Id, cancellationToken);
        if (user is null || credential is null)
        {
            return IdentityErrors.InvalidCredentials;
        }

        var verified = await Credentials.VerifyAsync(db, hasher, clock, credential, user, command.Password, cancellationToken);
        if (!verified.IsSuccess)
        {
            return verified.Error!;
        }

        var roles = await RolesInTenantAsync(db, user.Id, cancellationToken);
        var staffRoles = roles.Where(Roles.Staff.Contains).ToList();
        if (staffRoles.Count == 0)
        {
            return IdentityErrors.InvalidCredentials;
        }

        return new SignedIn(user.Id, tenantId, user.DisplayName, staffRoles, MustChangePassword: false);
    }

    internal static async Task<List<string>> RolesInTenantAsync(IIdentityDbContext db, Guid userId, CancellationToken cancellationToken) =>
        await db.Memberships
            .Where(m => m.UserId == userId && m.Status == MembershipStatus.Active)
            .SelectMany(m => m.Roles.Select(r => r.Role))
            .Distinct()
            .ToListAsync(cancellationToken);
}

internal static class Credentials
{
    public static async Task<Result<bool>> VerifyAsync(
        IIdentityDbContext db, IPasswordHasher<User> hasher, TimeProvider clock, LockableCredential credential, User user,
        string? password, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        if (credential.IsLocked(now))
        {
            return IdentityErrors.Locked;
        }

        if (hasher.VerifyHashedPassword(user, credential.PasswordHash, password ?? string.Empty) == PasswordVerificationResult.Failed)
        {
            credential.RegisterFailure(now);
            await db.SaveChangesAsync(cancellationToken);
            return credential.IsLocked(now) ? IdentityErrors.Locked : IdentityErrors.InvalidCredentials;
        }

        credential.RegisterSuccess();
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}

public sealed record LearnerLoginCommand(string Username, string Password);

public sealed class LearnerLoginHandler(IIdentityDbContext db, ITenantContext tenant, IPasswordHasher<User> hasher, TimeProvider clock)
{
    public async Task<Result<SignedIn>> HandleAsync(LearnerLoginCommand command, CancellationToken cancellationToken)
    {
        if (tenant.TenantId is not { } tenantId)
        {
            return IdentityErrors.TenantUnknown;
        }

        var username = LocalCredential.NormalizeUsername(command.Username ?? string.Empty);
        var credential = await db.LocalCredentials.FirstOrDefaultAsync(c => c.Username == username, cancellationToken);
        var user = credential is null ? null : await db.Users.FirstOrDefaultAsync(u => u.Id == credential.UserId, cancellationToken);
        if (credential is null || user is null)
        {
            return IdentityErrors.InvalidCredentials;
        }

        var verified = await Credentials.VerifyAsync(db, hasher, clock, credential, user, command.Password, cancellationToken);
        if (!verified.IsSuccess)
        {
            return verified.Error!;
        }

        var learnRoles = (await StaffLoginHandler.RolesInTenantAsync(db, user.Id, cancellationToken)).Where(Roles.Learn.Contains).ToList();
        return learnRoles.Count == 0
            ? IdentityErrors.InvalidCredentials
            : new SignedIn(user.Id, tenantId, user.DisplayName, learnRoles, credential.MustChangePassword);
    }
}

public sealed record ChangePasswordCommand(string CurrentPassword, string NewPassword);

public sealed class ChangeLearnerPasswordHandler(IIdentityDbContext db, IPasswordHasher<User> hasher, TimeProvider clock)
{
    public const int MinLength = 8;

    public async Task<Result<bool>> HandleAsync(Guid userId, ChangePasswordCommand command, CancellationToken cancellationToken)
    {
        if ((command.NewPassword ?? string.Empty).Length < MinLength)
        {
            return Error.Validation("identity.password_too_short", new Dictionary<string, object?> { ["min"] = MinLength });
        }

        var credential = await db.LocalCredentials.FirstOrDefaultAsync(c => c.UserId == userId, cancellationToken);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (credential is null || user is null)
        {
            return Error.NotFound("identity.account_not_found");
        }

        var verified = await Credentials.VerifyAsync(db, hasher, clock, credential, user, command.CurrentPassword, cancellationToken);
        if (!verified.IsSuccess)
        {
            return verified.Error!;
        }

        credential.ChangePassword(hasher.HashPassword(user, command.NewPassword!));
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}

public sealed record CreateLearnerAccountCommand(string DisplayName, string Role, string? Username, string? PreferredLocale);

public sealed record LearnerAccountCreated(Guid UserId, string Username, string TemporaryPassword);

/// <summary>Center-issued account for a student or parent without Google sign-in. The temporary password is shown once.</summary>
public sealed class CreateLearnerAccountHandler(IIdentityDbContext db, IPasswordHasher<User> hasher)
{
    private const string PasswordAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";

    public async Task<Result<LearnerAccountCreated>> HandleAsync(CreateLearnerAccountCommand command, CancellationToken cancellationToken)
    {
        if (!Roles.Learn.Contains(command.Role))
        {
            return Error.Validation("identity.role_invalid");
        }

        if (string.IsNullOrWhiteSpace(command.DisplayName))
        {
            return Error.Validation("identity.display_name_required");
        }

        var username = string.IsNullOrWhiteSpace(command.Username)
            ? await GenerateUsernameAsync(cancellationToken)
            : LocalCredential.NormalizeUsername(command.Username);
        if (await db.LocalCredentials.AnyAsync(c => c.Username == username, cancellationToken))
        {
            return Error.Conflict("identity.username_taken", new Dictionary<string, object?> { ["username"] = username });
        }

        var user = User.Create(email: null, command.DisplayName, command.PreferredLocale ?? "vi");
        var temporaryPassword = RandomNumberGenerator.GetString(PasswordAlphabet, 10);
        var membership = new Membership(user.Id);
        membership.Grant(command.Role);

        db.Users.Add(user);
        db.LocalCredentials.Add(new LocalCredential(user.Id, username, hasher.HashPassword(user, temporaryPassword)));
        db.Memberships.Add(membership);
        db.Publish(new LearnerAccountCreatedV1(user.Id, username, command.Role));
        await db.SaveChangesAsync(cancellationToken);
        return new LearnerAccountCreated(user.Id, username, temporaryPassword);
    }

    private async Task<string> GenerateUsernameAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var candidate = $"hv{RandomNumberGenerator.GetInt32(100000, 999999)}";
            if (!await db.LocalCredentials.AnyAsync(c => c.Username == candidate, cancellationToken))
            {
                return candidate;
            }
        }
    }
}

public sealed record BootstrapTenantAdminCommand(Guid TenantId, string Email, string DisplayName, string Password);

/// <summary>Platform operation: first administrator of a new tenant. Must run in a scope whose tenant is the target tenant.</summary>
public sealed class BootstrapTenantAdminHandler(IIdentityDbContext db, IPasswordHasher<User> hasher)
{
    public async Task<Result<Guid>> HandleAsync(BootstrapTenantAdminCommand command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Email) || (command.Password ?? string.Empty).Length < ChangeLearnerPasswordHandler.MinLength)
        {
            return Error.Validation("identity.bootstrap_invalid");
        }

        var email = User.NormalizeEmail(command.Email);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, cancellationToken);
        if (user is null)
        {
            user = User.Create(email, command.DisplayName, "vi");
            db.Users.Add(user);
        }

        if (!await db.PasswordCredentials.AnyAsync(c => c.UserId == user.Id, cancellationToken))
        {
            db.PasswordCredentials.Add(new PasswordCredential(user.Id, hasher.HashPassword(user, command.Password!)));
        }

        var membership = await db.Memberships.Include(m => m.Roles).FirstOrDefaultAsync(m => m.UserId == user.Id, cancellationToken);
        if (membership is null)
        {
            membership = new Membership(user.Id);
            db.Memberships.Add(membership);
        }

        membership.Grant(Roles.TenantAdmin);
        await db.SaveChangesAsync(cancellationToken);
        return user.Id;
    }
}

public sealed record AddStaffMemberCommand(string Email, string DisplayName, string? InitialPassword, IReadOnlyList<string> Roles);

/// <summary>Tenant admin adds an employee or teacher. An existing platform user (e.g. a freelance teacher) keeps their password.</summary>
public sealed class AddStaffMemberHandler(IIdentityDbContext db, IPasswordHasher<User> hasher)
{
    public async Task<Result<Guid>> HandleAsync(AddStaffMemberCommand command, CancellationToken cancellationToken)
    {
        if (command.Roles is not { Count: > 0 } || command.Roles.Any(r => !Roles.Staff.Contains(r)))
        {
            return Error.Validation("identity.role_invalid");
        }

        if (string.IsNullOrWhiteSpace(command.Email) || string.IsNullOrWhiteSpace(command.DisplayName))
        {
            return Error.Validation("identity.staff_invalid");
        }

        var email = User.NormalizeEmail(command.Email);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, cancellationToken);
        if (user is null)
        {
            if ((command.InitialPassword ?? string.Empty).Length < ChangeLearnerPasswordHandler.MinLength)
            {
                return Error.Validation("identity.password_too_short", new Dictionary<string, object?> { ["min"] = ChangeLearnerPasswordHandler.MinLength });
            }

            user = User.Create(email, command.DisplayName, "vi");
            db.Users.Add(user);
            db.PasswordCredentials.Add(new PasswordCredential(user.Id, hasher.HashPassword(user, command.InitialPassword!)));
        }

        var membership = await db.Memberships.Include(m => m.Roles).FirstOrDefaultAsync(m => m.UserId == user.Id, cancellationToken);
        if (membership is null)
        {
            membership = new Membership(user.Id);
            db.Memberships.Add(membership);
        }

        foreach (var role in command.Roles)
        {
            membership.Grant(role);
        }

        await db.SaveChangesAsync(cancellationToken);
        return user.Id;
    }
}

public sealed class IdentityQueries(IIdentityDbContext db, ITenantContext tenant) : IIdentityApi
{
    public async Task<MeDto?> GetMemberAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (tenant.TenantId is not { } tenantId)
        {
            return null;
        }

        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return null;
        }

        var roles = await StaffLoginHandler.RolesInTenantAsync(db, userId, cancellationToken);
        return roles.Count == 0 ? null : new MeDto(user.Id, user.DisplayName, user.Email, user.PreferredLocale, tenantId, roles);
    }

    public async Task<IReadOnlyList<Guid>> ListUsersWithRoleAsync(string role, CancellationToken cancellationToken) =>
        await db.Memberships
            .Where(m => m.Status == MembershipStatus.Active && m.Roles.Any(r => r.Role == role))
            .OrderBy(m => m.CreatedAt)
            .Select(m => m.UserId)
            .ToListAsync(cancellationToken);
}
