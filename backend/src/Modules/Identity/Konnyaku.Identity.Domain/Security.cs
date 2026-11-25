using Konnyaku.SharedKernel;

namespace Konnyaku.Identity.Domain;

/// <summary>
/// One sign-in on one device. Every access token names its session; signing out, a new password, locking the account or
/// changing the member's roles revokes it, and the API refuses the token from then on.
/// </summary>
public sealed class UserSession : Entity, ITenantOwned
{
    private UserSession()
    {
    }

    public Guid TenantId { get; private set; }
    public Guid UserId { get; private set; }

    /// <summary>Sign-in realm: <c>staff</c> or <c>learn</c>.</summary>
    public string Audience { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset LastSeenAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public string? RevokedReason { get; private set; }
    public string? UserAgent { get; private set; }
    public string? IpAddress { get; private set; }

    /// <summary>Security stamp of the credential at sign-in: a later password change ends the session.</summary>
    public Guid CredentialStamp { get; private set; }

    public static UserSession Start(Guid userId, string audience, Guid credentialStamp, DateTimeOffset now, DateTimeOffset expiresAt,
        string? userAgent, string? ipAddress) => new()
    {
        UserId = userId,
        Audience = audience,
        CredentialStamp = credentialStamp,
        CreatedAt = now,
        LastSeenAt = now,
        ExpiresAt = expiresAt,
        UserAgent = Truncate(userAgent, 300),
        IpAddress = Truncate(ipAddress, 64),
    };

    public bool IsOpen(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;

    public void Revoke(string reason, DateTimeOffset now)
    {
        if (RevokedAt is null)
        {
            RevokedAt = now;
            RevokedReason = reason;
        }
    }

    /// <summary>The owner changed the password on this device: the session stays, with the new stamp.</summary>
    public void Restamp(Guid credentialStamp) => CredentialStamp = credentialStamp;

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= max ? value : value[..max];
}

public static class SessionRevokeReasons
{
    public const string SignedOut = "SignedOut";
    public const string RevokedByOwner = "RevokedByOwner";
    public const string PasswordChanged = "PasswordChanged";
    public const string PasswordReset = "PasswordReset";
    public const string AccountSuspended = "AccountSuspended";
    public const string RolesChanged = "RolesChanged";
    public const string TwoFactorReset = "TwoFactorReset";
    public const string SignedOutByAdmin = "SignedOutByAdmin";
}

/// <summary>Self-service reset link (staff, by email): stored as a hash, single use, short-lived.</summary>
public sealed class PasswordResetToken : Entity, ITenantOwned
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    private PasswordResetToken()
    {
    }

    public PasswordResetToken(Guid userId, string tokenHash, DateTimeOffset now, string? requestedFromIp)
    {
        UserId = userId;
        TokenHash = tokenHash;
        CreatedAt = now;
        ExpiresAt = now.Add(Lifetime);
        RequestedFromIp = requestedFromIp is { Length: > 64 } ip ? ip[..64] : requestedFromIp;
    }

    public Guid TenantId { get; private set; }
    public Guid UserId { get; private set; }
    public string TokenHash { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? UsedAt { get; private set; }
    public string? RequestedFromIp { get; private set; }

    public bool IsUsable(DateTimeOffset now) => UsedAt is null && ExpiresAt > now;

    /// <summary>Used, or voided because another link was used or the password changed.</summary>
    public void Consume(DateTimeOffset now) => UsedAt ??= now;
}

/// <summary>
/// Security log of a center (append-only): sign-ins, failures, locks, password and 2FA changes, sign-outs. Read by the
/// administrator in ops.
/// </summary>
public sealed class SecurityEvent : Entity, ITenantOwned
{
    private SecurityEvent()
    {
    }

    public SecurityEvent(string kind, Guid? userId, string? subject, Guid? actorId, string? actorName, string? ipAddress, string? userAgent,
        string? detail, DateTimeOffset now)
    {
        Kind = kind;
        UserId = userId;
        Subject = Cut(subject, 320);
        ActorId = actorId;
        ActorName = Cut(actorName, 200);
        IpAddress = Cut(ipAddress, 64);
        UserAgent = Cut(userAgent, 300);
        Detail = Cut(detail, 200);
        OccurredAt = now;
    }

    public Guid TenantId { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
    public string Kind { get; private set; } = string.Empty;

    /// <summary>The account concerned, when known (a failed sign-in with an unknown email has none).</summary>
    public Guid? UserId { get; private set; }

    /// <summary>What was typed at sign-in (email or username) or the account's name.</summary>
    public string? Subject { get; private set; }

    /// <summary>Who did it, when not the account owner (an administrator).</summary>
    public Guid? ActorId { get; private set; }

    public string? ActorName { get; private set; }
    public string? IpAddress { get; private set; }
    public string? UserAgent { get; private set; }

    /// <summary>A short machine code (failure reason, realm), never free text.</summary>
    public string? Detail { get; private set; }

    private static string? Cut(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= max ? value : value[..max];
}

public static class SecurityEventKinds
{
    public const string SignInSucceeded = "SignInSucceeded";
    public const string SignInFailed = "SignInFailed";
    public const string AccountLockedOut = "AccountLockedOut";
    public const string AccountSuspended = "AccountSuspended";
    public const string AccountReactivated = "AccountReactivated";
    public const string PasswordChanged = "PasswordChanged";
    public const string PasswordResetByAdmin = "PasswordResetByAdmin";
    public const string PasswordResetRequested = "PasswordResetRequested";
    public const string PasswordResetCompleted = "PasswordResetCompleted";
    public const string TwoFactorEnabled = "TwoFactorEnabled";
    public const string TwoFactorDisabled = "TwoFactorDisabled";
    public const string TwoFactorResetByAdmin = "TwoFactorResetByAdmin";
    public const string RecoveryCodesRegenerated = "RecoveryCodesRegenerated";
    public const string RecoveryCodeUsed = "RecoveryCodeUsed";
    public const string SignedOut = "SignedOut";
    public const string SessionRevoked = "SessionRevoked";
    public const string SignedOutEverywhere = "SignedOutEverywhere";

    public static readonly IReadOnlyList<string> All =
    [
        SignInSucceeded, SignInFailed, AccountLockedOut, AccountSuspended, AccountReactivated, PasswordChanged, PasswordResetByAdmin,
        PasswordResetRequested, PasswordResetCompleted, TwoFactorEnabled, TwoFactorDisabled, TwoFactorResetByAdmin,
        RecoveryCodesRegenerated, RecoveryCodeUsed, SignedOut, SessionRevoked, SignedOutEverywhere,
    ];
}

/// <summary>
/// Minimum password rules shared by both realms: 8 to 128 characters, at least one letter and one digit, not a well-known
/// password.
/// </summary>
public static class PasswordPolicy
{
    public const int MinLength = 8;
    public const int MaxLength = 128;

    private static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        "password1", "password123", "passw0rd", "12345678a", "a12345678", "abc12345", "abcd1234", "qwerty123", "1qaz2wsx",
        "matkhau1", "matkhau123", "iloveyou1", "admin123", "admin1234", "welcome1", "letmein1", "123456789a", "1234qwer",
    };

    public static Error? Check(string? password)
    {
        var value = password ?? string.Empty;
        if (value.Length < MinLength)
        {
            return Error.Validation("identity.password_too_short", new Dictionary<string, object?> { ["min"] = MinLength });
        }

        if (value.Length > MaxLength)
        {
            return Error.Validation("identity.password_too_long", new Dictionary<string, object?> { ["max"] = MaxLength });
        }

        if (!value.Any(char.IsLetter) || !value.Any(char.IsDigit) || Common.Contains(value))
        {
            return Error.Validation("identity.password_too_weak");
        }

        return null;
    }
}
