using Konnyaku.SharedKernel;

namespace Konnyaku.Identity.Domain;

/// <summary>A person, platform-wide. Children may have no email; they sign in with a center-issued account.</summary>
public sealed class User : Entity, IAuditable
{
    private User()
    {
    }

    [PersonalData(PersonalDataKind.Email)]
    public string? Email { get; private set; }

    [PersonalData(PersonalDataKind.Name)]
    public string DisplayName { get; private set; } = string.Empty;

    public string PreferredLocale { get; private set; } = "vi";
    public DateTimeOffset CreatedAt { get; private set; }

    public static User Create(string? email, string displayName, string preferredLocale) => new()
    {
        Email = string.IsNullOrWhiteSpace(email) ? null : NormalizeEmail(email),
        DisplayName = displayName.Trim(),
        PreferredLocale = preferredLocale,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    Guid IAuditable.AuditSubjectId => Id;

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    /// <summary>Erasure of a learner: the account keeps its id (records point to it) but no name or email.</summary>
    public void Anonymize()
    {
        DisplayName = "#" + Id.ToString("N")[^8..];
        Email = null;
    }

    public Result<User> Rename(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return Error.Validation("identity.staff_invalid");
        }

        DisplayName = displayName.Trim();
        return this;
    }
}

/// <summary>Shared lockout rule: 5 failed attempts lock the credential for 15 minutes.</summary>
public abstract class LockableCredential : Entity
{
    public const int MaxFailedAttempts = 5;
    public static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(15);

    public string PasswordHash { get; protected set; } = string.Empty;
    public int FailedAttempts { get; private set; }
    public DateTimeOffset? LockedUntil { get; private set; }

    public bool IsLocked(DateTimeOffset now) => LockedUntil > now;

    public void RegisterFailure(DateTimeOffset now)
    {
        FailedAttempts++;
        if (FailedAttempts >= MaxFailedAttempts)
        {
            LockedUntil = now.Add(LockDuration);
            FailedAttempts = 0;
        }
    }

    public void RegisterSuccess()
    {
        FailedAttempts = 0;
        LockedUntil = null;
    }
}

/// <summary>Email + password sign-in (staff). Platform-wide, attached to a user.</summary>
public sealed class PasswordCredential : LockableCredential
{
    private PasswordCredential()
    {
    }

    public PasswordCredential(Guid userId, string passwordHash)
    {
        UserId = userId;
        PasswordHash = passwordHash;
    }

    public Guid UserId { get; private set; }
}

/// <summary>Username + password issued by a center; valid only on that tenant's hosts.</summary>
public sealed class LocalCredential : LockableCredential, ITenantOwned
{
    private LocalCredential()
    {
    }

    public LocalCredential(Guid userId, string username, string passwordHash)
    {
        UserId = userId;
        Username = NormalizeUsername(username);
        PasswordHash = passwordHash;
        MustChangePassword = true;
    }

    public Guid TenantId { get; private set; }
    public Guid UserId { get; private set; }
    public string Username { get; private set; } = string.Empty;
    public bool MustChangePassword { get; private set; }

    public void ChangePassword(string passwordHash)
    {
        PasswordHash = passwordHash;
        MustChangePassword = false;
    }

    public static string NormalizeUsername(string username) => username.Trim().ToLowerInvariant();
}

public sealed class Membership : Entity, ITenantOwned, IAuditable
{
    private Membership()
    {
    }

    public Membership(Guid userId)
    {
        UserId = userId;
        Status = MembershipStatus.Active;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid TenantId { get; private set; }
    public Guid UserId { get; private set; }
    public MembershipStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public List<RoleAssignment> Roles { get; private set; } = [];

    Guid IAuditable.AuditSubjectId => UserId;

    public void Grant(string role, ScopeType scopeType = ScopeType.Tenant, Guid? scopeId = null)
    {
        if (!Roles.Any(r => r.Role == role && r.ScopeType == scopeType && r.ScopeId == scopeId))
        {
            Roles.Add(new RoleAssignment(Id, role, scopeType, scopeId));
        }
    }

    /// <summary>Replaces the staff roles (learner roles are kept). A role with a branch is limited to that branch.</summary>
    public void ReplaceStaffRoles(IReadOnlyCollection<(string Role, Guid? BranchId)> grants)
    {
        Roles.RemoveAll(r => Domain.Roles.Staff.Contains(r.Role) &&
                             !grants.Any(g => g.Role == r.Role && g.BranchId == r.ScopeId && ScopeOf(g.BranchId) == r.ScopeType));
        foreach (var (role, branchId) in grants)
        {
            Grant(role, ScopeOf(branchId), branchId);
        }
    }

    public void Suspend() => Status = MembershipStatus.Suspended;

    public void Activate() => Status = MembershipStatus.Active;

    private static ScopeType ScopeOf(Guid? branchId) => branchId is null ? ScopeType.Tenant : ScopeType.Branch;
}

public enum MembershipStatus
{
    Invited,
    Active,
    Suspended,
}

public enum ScopeType
{
    Tenant,
    BranchGroup,
    Branch,
}

public sealed class RoleAssignment : Entity, ITenantOwned
{
    private RoleAssignment()
    {
    }

    internal RoleAssignment(Guid membershipId, string role, ScopeType scopeType, Guid? scopeId)
    {
        MembershipId = membershipId;
        Role = role;
        ScopeType = scopeType;
        ScopeId = scopeId;
    }

    public Guid TenantId { get; private set; }
    public Guid MembershipId { get; private set; }
    public string Role { get; private set; } = string.Empty;
    public ScopeType ScopeType { get; private set; }
    public Guid? ScopeId { get; private set; }
}

/// <summary>Default roles. Staff roles sign in on the staff realm, learner roles on the learn realm.</summary>
public static class Roles
{
    public const string TenantAdmin = "TenantAdmin";
    public const string BranchManager = "BranchManager";
    public const string Coordinator = "Coordinator";
    public const string Consultant = "Consultant";
    public const string Accountant = "Accountant";
    public const string Teacher = "Teacher";
    public const string Student = "Student";
    public const string Parent = "Parent";

    public static readonly IReadOnlySet<string> Staff = new HashSet<string>
    {
        TenantAdmin, BranchManager, Coordinator, Consultant, Accountant, Teacher,
    };

    public static readonly IReadOnlySet<string> Learn = new HashSet<string> { Student, Parent };

    /// <summary>Roles that may be limited to chosen branches; the others always cover the whole center.</summary>
    public static readonly IReadOnlySet<string> BranchScoped = new HashSet<string> { BranchManager, Coordinator };
}
