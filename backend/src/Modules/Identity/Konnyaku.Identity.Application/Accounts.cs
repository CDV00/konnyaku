using System.Security.Cryptography;
using System.Text.Json;
using Konnyaku.Application.Tenancy;
using Konnyaku.Identity.Contracts;
using Konnyaku.Identity.Domain;
using Konnyaku.Organization.Contracts;
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
    DbSet<UserSession> UserSessions { get; }
    DbSet<PasswordResetToken> PasswordResetTokens { get; }
    DbSet<SecurityEvent> SecurityEvents { get; }

    /// <summary>Starts tracking new rows as added (DbContext.AddRange).</summary>
    void AddRange(IEnumerable<object> entities);

    void Publish(IIntegrationEvent integrationEvent, string? correlationId = null);

    void LogChange(string entityType, Guid entityId, Guid subjectId, string action, IReadOnlyList<(string Field, string? Old, string? New)> changes);

    /// <summary>How many centers the person belongs to, across tenants; null when the database cannot tell (treated as "more than one").</summary>
    Task<int?> CountCentersOfUserAsync(Guid userId, CancellationToken cancellationToken);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

/// <summary>A completed sign-in: the session the access token will name.</summary>
public sealed record SignedIn(Guid UserId, Guid TenantId, string DisplayName, IReadOnlyList<string> Roles, bool MustChangePassword,
    Guid SessionId, DateTimeOffset ExpiresAt);

internal static class IdentityErrors
{
    // One message for unknown user, wrong password and wrong realm: never reveal whether an account exists.
    public static readonly Error InvalidCredentials = Error.Validation("identity.invalid_credentials");
    public static readonly Error Locked = Error.Validation("identity.account_locked");
    public static readonly Error Suspended = Error.Validation("identity.account_suspended");
    public static readonly Error TenantUnknown = Error.Validation("identity.tenant_unknown");
    public static readonly Error ChallengeInvalid = Error.Validation("identity.sign_in_expired");
    public static readonly Error CodeInvalid = Error.Validation("identity.code_invalid");
}

public sealed record StaffLoginCommand(string Email, string Password);

/// <summary>
/// Password step of a staff sign-in. Done when the account has no second factor and its roles do not require one;
/// otherwise the caller gets a pending sign-in to finish with a TOTP code (or by enrolling 2FA first).
/// </summary>
public sealed class StaffLoginHandler(
    IIdentityDbContext db, ITenantContext tenant, IPasswordHasher<User> hasher, SessionManager sessions, SecurityLog log, TwoFactorPolicy policy,
    TimeProvider clock)
{
    public async Task<Result<StaffSignInResult>> HandleAsync(StaffLoginCommand command, CancellationToken cancellationToken)
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
            log.Record(SecurityEventKinds.SignInFailed, null, email, "unknown_account");
            await db.SaveChangesAsync(cancellationToken);
            return IdentityErrors.InvalidCredentials;
        }

        var verified = await Credentials.VerifyAsync(db, hasher, clock, log, credential, user, command.Password, email, cancellationToken);
        if (!verified.IsSuccess)
        {
            return verified.Error!;
        }

        var membership = await Credentials.MembershipAsync(db, user.Id, Roles.Staff, cancellationToken);
        if (membership.Error is { } refused)
        {
            log.Record(SecurityEventKinds.SignInFailed, user.Id, email, refused == IdentityErrors.Suspended ? "suspended" : "no_staff_role");
            await db.SaveChangesAsync(cancellationToken);
            return refused;
        }

        if (credential.TwoFactorEnabled)
        {
            return new StaffSignInResult(null, new PendingSignIn(user.Id, tenantId, PendingSignIn.Verify, credential.SecurityStamp));
        }

        if (policy.IsRequiredFor(membership.Roles))
        {
            return new StaffSignInResult(null, new PendingSignIn(user.Id, tenantId, PendingSignIn.Enroll, credential.SecurityStamp));
        }

        credential.RegisterSuccess();
        var signedIn = sessions.Start(user, tenantId, Realms.Staff, credential, membership.Roles);
        log.Record(SecurityEventKinds.SignInSucceeded, user.Id, email, Realms.Staff);
        await db.SaveChangesAsync(cancellationToken);
        return new StaffSignInResult(signedIn, null);
    }

    internal static async Task<List<string>> RolesInTenantAsync(IIdentityDbContext db, Guid userId, CancellationToken cancellationToken) =>
        await db.Memberships
            .Where(m => m.UserId == userId && m.Status == MembershipStatus.Active)
            .SelectMany(m => m.Roles.Select(r => r.Role))
            .Distinct()
            .ToListAsync(cancellationToken);

    internal static async Task<List<string>> ActiveStaffRolesAsync(IIdentityDbContext db, Guid userId, CancellationToken cancellationToken) =>
        (await RolesInTenantAsync(db, userId, cancellationToken)).Where(Roles.Staff.Contains).ToList();
}

internal static class Credentials
{
    /// <summary>
    /// Checks the password. A failure counts towards the lockout and is logged; success does not reset the counter yet
    /// (the sign-in may still need its second factor), the caller does once the sign-in completes.
    /// </summary>
    public static async Task<Result<bool>> VerifyAsync(
        IIdentityDbContext db, IPasswordHasher<User> hasher, TimeProvider clock, SecurityLog log, LockableCredential credential, User user,
        string? password, string? subject, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        if (credential.IsLocked(now))
        {
            log.Record(SecurityEventKinds.SignInFailed, user.Id, subject, "locked");
            await db.SaveChangesAsync(cancellationToken);
            return IdentityErrors.Locked;
        }

        if (hasher.VerifyHashedPassword(user, credential.PasswordHash, password ?? string.Empty) == PasswordVerificationResult.Failed)
        {
            log.Record(SecurityEventKinds.SignInFailed, user.Id, subject, "wrong_password");
            if (credential.RegisterFailure(now))
            {
                log.Record(SecurityEventKinds.AccountLockedOut, user.Id, subject);
            }

            await db.SaveChangesAsync(cancellationToken);
            return credential.IsLocked(now) ? IdentityErrors.Locked : IdentityErrors.InvalidCredentials;
        }

        return true;
    }

    /// <summary>The member's roles of one realm in this center; suspended members and members without such a role are refused.</summary>
    public static async Task<(List<string> Roles, Error? Error)> MembershipAsync(
        IIdentityDbContext db, Guid userId, IReadOnlySet<string> realmRoles, CancellationToken cancellationToken)
    {
        var membership = await db.Memberships.AsNoTracking()
            .Where(m => m.UserId == userId)
            .Select(m => new { m.Status, Roles = m.Roles.Select(r => r.Role).ToList() })
            .FirstOrDefaultAsync(cancellationToken);
        var roles = membership?.Roles.Where(realmRoles.Contains).Distinct().ToList() ?? [];
        if (roles.Count == 0)
        {
            return ([], IdentityErrors.InvalidCredentials);
        }

        return membership!.Status == MembershipStatus.Active ? (roles, null) : ([], IdentityErrors.Suspended);
    }
}

public sealed record LearnerLoginCommand(string Username, string Password);

public sealed class LearnerLoginHandler(
    IIdentityDbContext db, ITenantContext tenant, IPasswordHasher<User> hasher, SessionManager sessions, SecurityLog log, TimeProvider clock)
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
            log.Record(SecurityEventKinds.SignInFailed, null, username, "unknown_account");
            await db.SaveChangesAsync(cancellationToken);
            return IdentityErrors.InvalidCredentials;
        }

        var verified = await Credentials.VerifyAsync(db, hasher, clock, log, credential, user, command.Password, username, cancellationToken);
        if (!verified.IsSuccess)
        {
            return verified.Error!;
        }

        var membership = await Credentials.MembershipAsync(db, user.Id, Roles.Learn, cancellationToken);
        if (membership.Error is { } refused)
        {
            log.Record(SecurityEventKinds.SignInFailed, user.Id, username, refused == IdentityErrors.Suspended ? "suspended" : "no_learner_role");
            await db.SaveChangesAsync(cancellationToken);
            return refused;
        }

        credential.RegisterSuccess();
        var signedIn = sessions.Start(user, tenantId, Realms.Learn, credential, membership.Roles);
        log.Record(SecurityEventKinds.SignInSucceeded, user.Id, username, Realms.Learn);
        await db.SaveChangesAsync(cancellationToken);
        return signedIn;
    }
}

public sealed record ChangePasswordCommand(string CurrentPassword, string NewPassword);

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

/// <param name="Temporary">The password was generated for the person (platform console): changed at first sign-in.</param>
public sealed record BootstrapTenantAdminCommand(Guid TenantId, string Email, string DisplayName, string Password, bool Temporary = false);

/// <summary>Platform operation: first administrator of a new tenant. Must run in a scope whose tenant is the target tenant.</summary>
public sealed class BootstrapTenantAdminHandler(IIdentityDbContext db, IPasswordHasher<User> hasher)
{
    public async Task<Result<Guid>> HandleAsync(BootstrapTenantAdminCommand command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Email) || PasswordPolicy.Check(command.Password) is not null)
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
            db.PasswordCredentials.Add(new PasswordCredential(user.Id, hasher.HashPassword(user, command.Password!), command.Temporary));
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

/// <param name="BranchIds">Limits the branch-scoped roles (branch manager, coordinator) to these branches; empty = whole center.</param>
public sealed record AddStaffMemberCommand(string Email, string DisplayName, string? InitialPassword, IReadOnlyList<string> Roles,
    IReadOnlyList<Guid>? BranchIds = null);

/// <summary>Tenant admin adds an employee or teacher. An existing platform user (e.g. a freelance teacher) keeps their password.</summary>
public sealed class AddStaffMemberHandler(IIdentityDbContext db, IPasswordHasher<User> hasher, IOrganizationApi organization)
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

        var branchIds = command.BranchIds ?? [];
        if (await StaffRoleRules.FindUnknownBranchAsync(organization, branchIds, cancellationToken) is { } unknown)
        {
            return unknown;
        }

        var email = User.NormalizeEmail(command.Email);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, cancellationToken);
        if (user is null)
        {
            if (PasswordPolicy.Check(command.InitialPassword) is { } weak)
            {
                return weak;
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

        var existing = membership.Roles.ToHashSet();
        foreach (var role in command.Roles)
        {
            if (Roles.BranchScoped.Contains(role) && branchIds.Count > 0)
            {
                foreach (var branchId in branchIds)
                {
                    membership.Grant(role, ScopeType.Branch, branchId);
                }
            }
            else
            {
                membership.Grant(role);
            }
        }

        StaffRoleRules.TrackNew(db, membership, existing);
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

public sealed record StaffRoleView(string Role, Guid? BranchId);

public sealed record StaffMemberView(Guid UserId, string DisplayName, string? Email, IReadOnlyList<string> Roles, string Status,
    IReadOnlyList<StaffRoleView> Grants, bool TwoFactorEnabled = false);

public sealed class StaffQueries(IIdentityDbContext db)
{
    /// <summary>Staff members of the current tenant, suspended ones included (memberships are tenant-filtered; users are platform-wide).</summary>
    public async Task<IReadOnlyList<StaffMemberView>> ListAsync(CancellationToken cancellationToken)
    {
        var members = await db.Memberships.AsNoTracking()
            .Where(m => m.Status != MembershipStatus.Invited)
            .Select(m => new
            {
                m.UserId,
                m.Status,
                Grants = m.Roles.Select(r => new StaffRoleView(r.Role, r.ScopeType == ScopeType.Branch ? r.ScopeId : null)).ToList(),
            })
            .ToListAsync(cancellationToken);
        var staff = members
            .Select(m => (m.UserId, m.Status, Grants: m.Grants.Where(g => Roles.Staff.Contains(g.Role)).OrderBy(g => g.Role).ToList()))
            .Where(m => m.Grants.Count > 0)
            .ToDictionary(m => m.UserId);
        var ids = staff.Keys.ToList();
        var users = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToListAsync(cancellationToken);
        var twoFactor = (await db.PasswordCredentials.AsNoTracking()
            .Where(c => ids.Contains(c.UserId) && c.TotpSecret != null)
            .Select(c => c.UserId)
            .ToListAsync(cancellationToken)).ToHashSet();
        return users
            .Select(u => new StaffMemberView(u.Id, u.DisplayName, u.Email, staff[u.Id].Grants.Select(g => g.Role).Distinct().Order().ToList(),
                staff[u.Id].Status.ToString(), staff[u.Id].Grants, twoFactor.Contains(u.Id)))
            .OrderBy(s => s.Status != nameof(MembershipStatus.Active)).ThenBy(s => s.DisplayName, StringComparer.CurrentCulture)
            .ToList();
    }
}

public sealed record RoleGrantInput(string Role, Guid? BranchId);

public sealed record UpdateStaffMemberCommand(string DisplayName, IReadOnlyList<RoleGrantInput> Roles);

internal static class StaffRoleRules
{
    public static async Task<Error?> FindUnknownBranchAsync(IOrganizationApi organization, IEnumerable<Guid> branchIds, CancellationToken ct)
    {
        foreach (var branchId in branchIds.Distinct())
        {
            if (await organization.GetBranchAsync(branchId, ct) is null)
            {
                return Error.Validation("identity.branch_unknown");
            }
        }

        return null;
    }

    /// <summary>New role rows carry their own id: add them explicitly, or EF would take them for existing rows.</summary>
    public static void TrackNew(IIdentityDbContext db, Membership membership, HashSet<RoleAssignment> existing) =>
        db.AddRange(membership.Roles.Where(r => !existing.Contains(r)));

    public static string Describe(IEnumerable<(string Role, Guid? BranchId)> grants) =>
        JsonSerializer.Serialize(grants.OrderBy(g => g.Role, StringComparer.Ordinal).ThenBy(g => g.BranchId)
            .Select(g => new { role = g.Role, branchId = g.BranchId }));
}

/// <summary>
/// Tenant admin edits a staff member: name, roles and the branches a branch manager or coordinator is responsible for;
/// suspends or re-activates the membership (a suspended member cannot sign in; history and assignments stay).
/// The center always keeps at least one active administrator.
/// </summary>
public sealed class StaffAdminHandler(IIdentityDbContext db, IOrganizationApi organization, AccountAdminHandler accounts, SessionManager sessions)
{
    public async Task<Result<StaffMemberView>> UpdateAsync(Guid userId, UpdateStaffMemberCommand command, CancellationToken ct)
    {
        var grants = (command.Roles ?? []).Select(r => (r.Role, r.BranchId)).Distinct().ToList();
        if (grants.Count == 0 || grants.Any(g => !Roles.Staff.Contains(g.Role)))
        {
            return Error.Validation("identity.role_invalid");
        }

        if (grants.Any(g => g.BranchId is not null && !Roles.BranchScoped.Contains(g.Role)))
        {
            return Error.Validation("identity.role_scope_invalid");
        }

        var branchIds = grants.Where(g => g.BranchId is not null).Select(g => g.BranchId!.Value);
        if (await StaffRoleRules.FindUnknownBranchAsync(organization, branchIds, ct) is { } unknown)
        {
            return unknown;
        }

        var (user, membership) = await LoadAsync(userId, ct);
        if (user is null || membership is null)
        {
            return Error.NotFound("identity.staff_not_found");
        }

        var renamed = user.Rename(command.DisplayName ?? string.Empty);
        if (!renamed.IsSuccess)
        {
            return renamed.Error!;
        }

        var keepsAdmin = grants.Any(g => g.Role == Roles.TenantAdmin && g.BranchId is null);
        if (!keepsAdmin && IsAdmin(membership) && !await AnotherActiveAdminAsync(membership.Id, ct))
        {
            return Error.Conflict("identity.last_admin");
        }

        var before = StaffRoleRules.Describe(StaffGrants(membership));
        var existing = membership.Roles.ToHashSet();
        membership.ReplaceStaffRoles(grants);
        StaffRoleRules.TrackNew(db, membership, existing);
        var after = StaffRoleRules.Describe(StaffGrants(membership));
        IReadOnlyList<Guid> revoked = [];
        if (before != after)
        {
            db.LogChange(nameof(Membership), membership.Id, userId, "Updated", [("roles", before, after)]);
            // The token lists the old roles: the member signs in again with the new ones.
            revoked = await sessions.RevokeAllAsync(userId, SessionRevokeReasons.RolesChanged, ct, realm: Realms.Staff);
        }

        await sessions.SaveAsync(revoked, ct);
        return await ViewAsync(userId, ct);
    }

    public async Task<Result<StaffMemberView>> SuspendAsync(Guid userId, CancellationToken ct)
    {
        var (user, membership) = await LoadAsync(userId, ct);
        if (user is null || membership is null)
        {
            return Error.NotFound("identity.staff_not_found");
        }

        var suspended = await accounts.SuspendAsync(userId, ct);
        return suspended.IsSuccess ? await ViewAsync(userId, ct) : suspended.Error!;
    }

    public async Task<Result<StaffMemberView>> ActivateAsync(Guid userId, CancellationToken ct)
    {
        var (user, membership) = await LoadAsync(userId, ct);
        if (user is null || membership is null)
        {
            return Error.NotFound("identity.staff_not_found");
        }

        var activated = await accounts.ActivateAsync(userId, ct);
        return activated.IsSuccess ? await ViewAsync(userId, ct) : activated.Error!;
    }

    private async Task<(User? User, Membership? Membership)> LoadAsync(Guid userId, CancellationToken ct)
    {
        var membership = await db.Memberships.Include(m => m.Roles).FirstOrDefaultAsync(m => m.UserId == userId, ct);
        if (membership is null || !membership.Roles.Any(r => Roles.Staff.Contains(r.Role)))
        {
            return (null, null);
        }

        return (await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct), membership);
    }

    private static List<(string Role, Guid? BranchId)> StaffGrants(Membership membership) =>
        membership.Roles.Where(r => Roles.Staff.Contains(r.Role))
            .Select(r => (r.Role, r.ScopeType == ScopeType.Branch ? r.ScopeId : null)).ToList();

    private static bool IsAdmin(Membership membership) =>
        membership.Status == MembershipStatus.Active &&
        membership.Roles.Any(r => r.Role == Roles.TenantAdmin && r.ScopeType == ScopeType.Tenant);

    private Task<bool> AnotherActiveAdminAsync(Guid membershipId, CancellationToken ct) =>
        db.Memberships.AnyAsync(m => m.Id != membershipId && m.Status == MembershipStatus.Active &&
                                     m.Roles.Any(r => r.Role == Roles.TenantAdmin && r.ScopeType == ScopeType.Tenant), ct);

    private async Task<StaffMemberView> ViewAsync(Guid userId, CancellationToken ct) =>
        (await new StaffQueries(db).ListAsync(ct)).First(s => s.UserId == userId);
}
