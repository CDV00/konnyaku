using Konnyaku.SharedKernel;

namespace Konnyaku.Platform.Domain;

/// <summary>
/// A Konnyaku staff member who operates the platform (creates and suspends centers). Separate from tenant users:
/// own table, own sign-in realm (<c>platform</c>), password + mandatory TOTP.
/// </summary>
public sealed class PlatformOperator : Entity
{
    public const int MaxFailedAttempts = 5;
    public const int MinPasswordLength = 12;
    public static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(15);

    private PlatformOperator()
    {
    }

    public string Email { get; private set; } = string.Empty;
    public string DisplayName { get; private set; } = string.Empty;
    public string PasswordHash { get; private set; } = string.Empty;
    public bool MustChangePassword { get; private set; }

    /// <summary>Confirmed TOTP secret, encrypted at rest. Null until the operator completes enrollment.</summary>
    public string? TotpSecret { get; private set; }

    /// <summary>Secret shown during enrollment, encrypted; becomes <see cref="TotpSecret"/> once a code is verified.</summary>
    public string? PendingTotpSecret { get; private set; }

    /// <summary>Last accepted TOTP time step: a code is accepted once (no replay within its 30-second window).</summary>
    public long? LastTotpStep { get; private set; }

    /// <summary>SHA-256 hashes of the unused single-use recovery codes.</summary>
    public List<string> RecoveryCodeHashes { get; private set; } = [];

    public int FailedAttempts { get; private set; }
    public DateTimeOffset? LockedUntil { get; private set; }
    public OperatorStatus Status { get; private set; } = OperatorStatus.Active;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? LastSignInAt { get; private set; }
    public Guid? InvitedBy { get; private set; }

    public bool TotpEnrolled => TotpSecret is not null;

    /// <summary>Set the password with <see cref="ChangePassword"/> right after (the hasher needs the instance).</summary>
    public static Result<PlatformOperator> Create(string email, string displayName, Guid? invitedBy, DateTimeOffset now)
    {
        var normalized = NormalizeEmail(email);
        if (!IsValidEmail(normalized))
        {
            return Error.Validation("platform.email_invalid");
        }

        if (string.IsNullOrWhiteSpace(displayName))
        {
            return Error.Validation("platform.display_name_required");
        }

        return new PlatformOperator
        {
            Email = normalized,
            DisplayName = displayName.Trim(),
            InvitedBy = invitedBy,
            CreatedAt = now,
        };
    }

    public static string NormalizeEmail(string? email) => (email ?? string.Empty).Trim().ToLowerInvariant();

    public static bool IsValidEmail(string email)
    {
        var at = email.IndexOf('@');
        return at > 0 && at == email.LastIndexOf('@') && email.IndexOf('.', at) > at + 1 && !email.EndsWith('.') && email.Length <= 254;
    }

    public bool IsLocked(DateTimeOffset now) => LockedUntil > now;

    /// <summary>Wrong password, TOTP or recovery code. Five in a row lock the account for 15 minutes.</summary>
    public void RegisterFailure(DateTimeOffset now)
    {
        FailedAttempts++;
        if (FailedAttempts >= MaxFailedAttempts)
        {
            LockedUntil = now.Add(LockDuration);
            FailedAttempts = 0;
        }
    }

    /// <summary>Called after the second factor only: a correct password alone does not clear earlier failures.</summary>
    public void RegisterSignIn(DateTimeOffset now)
    {
        FailedAttempts = 0;
        LockedUntil = null;
        LastSignInAt = now;
    }

    public void StartTotpEnrollment(string protectedSecret) => PendingTotpSecret = protectedSecret;

    /// <summary>Activates the pending secret after a verified code and replaces all recovery codes.</summary>
    public void CompleteTotpEnrollment(long step, IReadOnlyCollection<string> recoveryCodeHashes)
    {
        TotpSecret = PendingTotpSecret ?? throw new InvalidOperationException("No TOTP enrollment in progress.");
        PendingTotpSecret = null;
        LastTotpStep = step;
        RecoveryCodeHashes = [.. recoveryCodeHashes];
    }

    /// <summary>Enrolls a known secret directly (bootstrap by automation, development seed).</summary>
    public void EnrollTotp(string protectedSecret, IReadOnlyCollection<string> recoveryCodeHashes)
    {
        TotpSecret = protectedSecret;
        PendingTotpSecret = null;
        LastTotpStep = null;
        RecoveryCodeHashes = [.. recoveryCodeHashes];
    }

    public bool AcceptTotpStep(long step)
    {
        if (LastTotpStep is { } last && step <= last)
        {
            return false;
        }

        LastTotpStep = step;
        return true;
    }

    public bool UseRecoveryCode(string codeHash)
    {
        // Replace the list so EF Core sees the change of the array column.
        if (!RecoveryCodeHashes.Contains(codeHash))
        {
            return false;
        }

        RecoveryCodeHashes = RecoveryCodeHashes.Where(h => h != codeHash).ToList();
        return true;
    }

    public void ChangePassword(string passwordHash)
    {
        PasswordHash = passwordHash;
        MustChangePassword = false;
    }

    /// <summary>A temporary password (invitation): the console only allows changing it until a new one is set.</summary>
    public void RequirePasswordChange() => MustChangePassword = true;

    public void Disable() => Status = OperatorStatus.Disabled;

    public void Enable() => Status = OperatorStatus.Active;
}

public enum OperatorStatus
{
    Active,
    Disabled,
}

/// <summary>
/// Append-only record of a platform operation: who (operator or automation key), when, what, on which tenant.
/// The <c>kk_platform</c> role may only insert and read this table.
/// </summary>
public sealed class PlatformAuditEntry : Entity
{
    public const string AutomationActor = "automation";

    private PlatformAuditEntry()
    {
    }

    public PlatformAuditEntry(
        DateTimeOffset at, Guid? operatorId, string actor, string action, Guid? tenantId,
        IReadOnlyDictionary<string, string?>? details, string? ipAddress)
    {
        At = at;
        OperatorId = operatorId;
        Actor = actor;
        Action = action;
        TenantId = tenantId;
        Details = details is null ? [] : new Dictionary<string, string?>(details);
        IpAddress = ipAddress;
    }

    public DateTimeOffset At { get; private set; }
    public Guid? OperatorId { get; private set; }

    /// <summary>Operator email at the time of the action, or <see cref="AutomationActor"/>.</summary>
    public string Actor { get; private set; } = string.Empty;

    /// <summary>Dotted action code (<c>tenant.suspend</c>), translated by the frontend.</summary>
    public string Action { get; private set; } = string.Empty;

    public Guid? TenantId { get; private set; }
    public Dictionary<string, string?> Details { get; private set; } = [];
    public string? IpAddress { get; private set; }
}

public static class AuditActions
{
    public const string OperatorBootstrap = "operator.bootstrap";
    public const string OperatorInvite = "operator.invite";
    public const string OperatorDisable = "operator.disable";
    public const string OperatorEnable = "operator.enable";
    public const string OperatorSignIn = "operator.sign_in";
    public const string OperatorLocked = "operator.locked";
    public const string OperatorTotpEnrolled = "operator.totp_enrolled";
    public const string OperatorRecoveryCodeUsed = "operator.recovery_code_used";
    public const string OperatorPasswordChanged = "operator.password_changed";
    public const string TenantCreate = "tenant.create";
    public const string TenantAdminCreate = "tenant.admin_create";
    public const string TenantSuspend = "tenant.suspend";
    public const string TenantResume = "tenant.resume";
    public const string TenantDomainAdd = "tenant.domain_add";
    public const string TenantDomainRemove = "tenant.domain_remove";
}
