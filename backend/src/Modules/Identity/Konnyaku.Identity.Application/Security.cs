using System.Security.Cryptography;
using System.Text;
using Konnyaku.Application.Authorization;
using Konnyaku.Application.Security;
using Konnyaku.Application.Tenancy;
using Konnyaku.Identity.Domain;
using Konnyaku.SharedKernel;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Konnyaku.Identity.Application;

/// <summary>The two sign-in realms (the same names as the token audiences).</summary>
public static class Realms
{
    public const string Staff = "staff";
    public const string Learn = "learn";
}

/// <summary>Who is calling: device, IP, acting member and the session of their token. Implemented over the HTTP request.</summary>
public interface IRequestContext
{
    string? IpAddress { get; }
    string? UserAgent { get; }
    Guid? ActorId { get; }
    string? ActorName { get; }
    Guid? SessionId { get; }
}

/// <summary>Forgets cached "session is active" answers right after a revocation (other API replicas follow within the cache time).</summary>
public interface ISessionCache
{
    void Evict(IEnumerable<Guid> sessionIds);
}

/// <summary>Staff roles that must sign in with a second factor (TOTP). Empty when enforcement is off (development, tests).</summary>
public sealed record TwoFactorPolicy(IReadOnlySet<string> RequiredRoles)
{
    public bool IsRequiredFor(IEnumerable<string> roles) => roles.Any(RequiredRoles.Contains);
}

public sealed record PasswordResetMail(Guid TokenId, Guid UserId, string Email, string DisplayName, string Locale, string ResetUrl,
    int ExpiresInMinutes);

/// <summary>
/// Sends the reset link. The Notification module's email API takes this over once it is available; until then the
/// default implementation only logs (the link itself in Development only).
/// </summary>
public interface IPasswordResetMailer
{
    Task SendAsync(PasswordResetMail mail, CancellationToken cancellationToken);
}

/// <summary>Adds entries to the center's security log (saved with the caller's changes).</summary>
public sealed class SecurityLog(IIdentityDbContext db, IRequestContext request, TimeProvider clock)
{
    public void Record(string kind, Guid? userId, string? subject = null, string? detail = null)
    {
        // The actor is recorded when someone acts on another person's account (an administrator).
        var byOther = request.ActorId is { } actor && actor != userId;
        db.SecurityEvents.Add(new SecurityEvent(kind, userId, subject, byOther ? request.ActorId : null, byOther ? request.ActorName : null,
            request.IpAddress, request.UserAgent, detail, clock.GetUtcNow()));
    }
}

/// <summary>Starts and revokes sign-in sessions.</summary>
public sealed class SessionManager(IIdentityDbContext db, IRequestContext request, ISessionCache cache, TimeProvider clock)
{
    public SignedIn Start(User user, Guid tenantId, string realm, LockableCredential credential, IReadOnlyList<string> roles)
    {
        var now = clock.GetUtcNow();
        var lifetime = realm == Realms.Learn ? TokenLifetimes.Learn : TokenLifetimes.Staff;
        var session = UserSession.Start(user.Id, realm, credential.SecurityStamp, now, now.Add(lifetime), request.UserAgent, request.IpAddress);
        db.UserSessions.Add(session);
        return new SignedIn(user.Id, tenantId, user.DisplayName, roles, credential.MustChangePassword, session.Id, session.ExpiresAt);
    }

    /// <summary>Revokes the user's open sessions in this center (optionally one realm only, or all but one session).</summary>
    public async Task<IReadOnlyList<Guid>> RevokeAllAsync(Guid userId, string reason, CancellationToken ct, Guid? except = null, string? realm = null)
    {
        var now = clock.GetUtcNow();
        var open = await db.UserSessions
            .Where(s => s.UserId == userId && s.RevokedAt == null && s.ExpiresAt > now && s.Id != except && (realm == null || s.Audience == realm))
            .ToListAsync(ct);
        foreach (var session in open)
        {
            session.Revoke(reason, now);
        }

        return open.Select(s => s.Id).ToList();
    }

    /// <summary>Saves, then drops the revoked sessions from the cache (after the commit, so no request re-caches them as active).</summary>
    public async Task SaveAsync(IEnumerable<Guid> revoked, CancellationToken ct)
    {
        await db.SaveChangesAsync(ct);
        cache.Evict(revoked);
    }
}

internal static class SecondFactor
{
    public const string Issuer = "Konnyaku";

    /// <summary>Checks a TOTP code (once per time step) or a single-use recovery code. Null when neither matches.</summary>
    public static string? Verify(PasswordCredential credential, ISecretProtector secrets, string? code, string? recoveryCode, DateTimeOffset now)
    {
        if (credential.TotpSecret is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(recoveryCode))
        {
            return credential.UseRecoveryCode(RecoveryCodes.Hash(recoveryCode)) ? "recovery" : null;
        }

        var step = Totp.Match(secrets.Unprotect(credential.TotpSecret), code, now);
        return step is { } s && credential.AcceptTotpStep(s) ? "totp" : null;
    }

    public static TwoFactorSetup Begin(PasswordCredential credential, ISecretProtector secrets, string account)
    {
        var secret = Totp.NewSecret();
        credential.BeginTotpEnrollment(secrets.Protect(secret));
        var base32 = Base32.Encode(secret);
        return new TwoFactorSetup(base32, Totp.OtpAuthUri(Issuer, account, base32));
    }

    /// <summary>Confirms the enrollment with a code from the new secret; returns the recovery codes (shown once).</summary>
    public static IReadOnlyList<string>? Confirm(PasswordCredential credential, ISecretProtector secrets, string? code, DateTimeOffset now)
    {
        if (credential.PendingTotpSecret is null || Totp.Match(secrets.Unprotect(credential.PendingTotpSecret), code, now) is not { } step)
        {
            return null;
        }

        var codes = RecoveryCodes.Generate();
        credential.EnableTotp(step, codes.Select(RecoveryCodes.Hash), now);
        return codes;
    }
}

public sealed record TwoFactorSetup(string Secret, string OtpAuthUri);

/// <summary>What the password step of a staff sign-in still needs: a code (<c>verify</c>) or enrolling 2FA first (<c>enroll</c>).</summary>
public sealed record PendingSignIn(Guid UserId, Guid TenantId, string Purpose, Guid Stamp)
{
    public const string Verify = "verify";
    public const string Enroll = "enroll";
}

public sealed record StaffSignInResult(SignedIn? SignedIn, PendingSignIn? Pending);

public sealed record SecondFactorCommand(string Challenge, string? Code, string? RecoveryCode);

public sealed record TwoFactorEnrolled(IReadOnlyList<string> RecoveryCodes, SignedIn SignedIn);

/// <summary>
/// Second step of a staff sign-in: the TOTP code (or a recovery code) of an account with 2FA, or — for a role that must use
/// 2FA — enrolling an authenticator before the first session is issued. Wrong codes count towards the account lockout.
/// </summary>
public sealed class StaffSecondFactorHandler(
    IIdentityDbContext db, ITenantContext tenant, ISecretProtector secrets, SessionManager sessions, SecurityLog log, TimeProvider clock)
{
    public async Task<Result<SignedIn>> VerifyAsync(PendingSignIn pending, SecondFactorCommand command, CancellationToken ct)
    {
        var loaded = await LoadAsync(pending, PendingSignIn.Verify, ct);
        if (!loaded.IsSuccess)
        {
            return loaded.Error!;
        }

        var (user, credential) = loaded.Value;
        var now = clock.GetUtcNow();
        var method = SecondFactor.Verify(credential, secrets, command.Code, command.RecoveryCode, now);
        if (method is null)
        {
            return await FailAsync(user, credential, now, ct);
        }

        if (method == "recovery")
        {
            log.Record(SecurityEventKinds.RecoveryCodeUsed, user.Id, user.Email);
        }

        return await CompleteAsync(user, credential, ct);
    }

    public async Task<Result<TwoFactorSetup>> SetupAsync(PendingSignIn pending, CancellationToken ct)
    {
        var loaded = await LoadAsync(pending, PendingSignIn.Enroll, ct);
        if (!loaded.IsSuccess)
        {
            return loaded.Error!;
        }

        var (user, credential) = loaded.Value;
        var setup = SecondFactor.Begin(credential, secrets, user.Email ?? user.DisplayName);
        await db.SaveChangesAsync(ct);
        return setup;
    }

    public async Task<Result<TwoFactorEnrolled>> EnableAsync(PendingSignIn pending, SecondFactorCommand command, CancellationToken ct)
    {
        var loaded = await LoadAsync(pending, PendingSignIn.Enroll, ct);
        if (!loaded.IsSuccess)
        {
            return loaded.Error!;
        }

        var (user, credential) = loaded.Value;
        var now = clock.GetUtcNow();
        var codes = SecondFactor.Confirm(credential, secrets, command.Code, now);
        if (codes is null)
        {
            return await FailAsync(user, credential, now, ct);
        }

        log.Record(SecurityEventKinds.TwoFactorEnabled, user.Id, user.Email);
        var signedIn = await CompleteAsync(user, credential, ct);
        return signedIn.IsSuccess ? new TwoFactorEnrolled(codes, signedIn.Value) : signedIn.Error!;
    }

    private async Task<Result<(User, PasswordCredential)>> LoadAsync(PendingSignIn pending, string purpose, CancellationToken ct)
    {
        if (tenant.TenantId != pending.TenantId || pending.Purpose != purpose)
        {
            return IdentityErrors.ChallengeInvalid;
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == pending.UserId, ct);
        var credential = await db.PasswordCredentials.FirstOrDefaultAsync(c => c.UserId == pending.UserId, ct);
        if (user is null || credential is null || credential.SecurityStamp != pending.Stamp)
        {
            return IdentityErrors.ChallengeInvalid;
        }

        if (credential.IsLocked(clock.GetUtcNow()))
        {
            return IdentityErrors.Locked;
        }

        return (user, credential);
    }

    private async Task<Error> FailAsync(User user, PasswordCredential credential, DateTimeOffset now, CancellationToken ct)
    {
        log.Record(SecurityEventKinds.SignInFailed, user.Id, user.Email, "wrong_code");
        if (credential.RegisterFailure(now))
        {
            log.Record(SecurityEventKinds.AccountLockedOut, user.Id, user.Email);
        }

        await db.SaveChangesAsync(ct);
        return credential.IsLocked(now) ? IdentityErrors.Locked : IdentityErrors.CodeInvalid;
    }

    private async Task<Result<SignedIn>> CompleteAsync(User user, PasswordCredential credential, CancellationToken ct)
    {
        var roles = await StaffLoginHandler.ActiveStaffRolesAsync(db, user.Id, ct);
        if (roles.Count == 0)
        {
            return IdentityErrors.InvalidCredentials;
        }

        credential.RegisterSuccess();
        var signedIn = sessions.Start(user, tenant.RequiredTenantId, Realms.Staff, credential, roles);
        log.Record(SecurityEventKinds.SignInSucceeded, user.Id, user.Email, Realms.Staff);
        await db.SaveChangesAsync(ct);
        return signedIn;
    }
}

public sealed record SessionView(Guid Id, string Audience, DateTimeOffset CreatedAt, DateTimeOffset LastSeenAt, DateTimeOffset ExpiresAt,
    string? UserAgent, string? IpAddress, bool Current);

public sealed record MySecurityView(bool TwoFactorEnabled, bool TwoFactorRequired, int RecoveryCodesLeft, bool MustChangePassword,
    IReadOnlyList<SessionView> Sessions);

/// <summary>The signed-in member's own sessions, password and second factor.</summary>
public sealed class MyAccountHandler(
    IIdentityDbContext db, IPasswordHasher<User> hasher, ISecretProtector secrets, SessionManager sessions, SecurityLog log,
    IRequestContext request, TwoFactorPolicy policy, TimeProvider clock)
{
    public async Task<MySecurityView> SecurityAsync(Guid userId, string realm, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var list = await db.UserSessions.AsNoTracking()
            .Where(s => s.UserId == userId && s.RevokedAt == null && s.ExpiresAt > now)
            .OrderByDescending(s => s.LastSeenAt)
            .ToListAsync(ct);
        var views = list.Select(s => new SessionView(s.Id, s.Audience, s.CreatedAt, s.LastSeenAt, s.ExpiresAt, s.UserAgent, s.IpAddress,
            s.Id == request.SessionId)).ToList();
        if (realm == Realms.Learn)
        {
            var local = await db.LocalCredentials.AsNoTracking().FirstOrDefaultAsync(c => c.UserId == userId, ct);
            return new MySecurityView(false, false, 0, local?.MustChangePassword ?? false, views);
        }

        var credential = await db.PasswordCredentials.AsNoTracking().FirstOrDefaultAsync(c => c.UserId == userId, ct);
        var roles = await StaffLoginHandler.ActiveStaffRolesAsync(db, userId, ct);
        return new MySecurityView(credential?.TwoFactorEnabled ?? false, policy.IsRequiredFor(roles), credential?.RecoveryCodeHashes.Count ?? 0,
            credential?.MustChangePassword ?? false, views);
    }

    public async Task<Result<bool>> SignOutAsync(Guid userId, CancellationToken ct)
    {
        var session = request.SessionId is { } id ? await db.UserSessions.FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId, ct) : null;
        if (session is null)
        {
            return true;
        }

        session.Revoke(SessionRevokeReasons.SignedOut, clock.GetUtcNow());
        log.Record(SecurityEventKinds.SignedOut, userId, detail: session.Audience);
        await sessions.SaveAsync([session.Id], ct);
        return true;
    }

    public async Task<Result<bool>> RevokeSessionAsync(Guid userId, Guid sessionId, CancellationToken ct)
    {
        var session = await db.UserSessions.FirstOrDefaultAsync(s => s.Id == sessionId && s.UserId == userId, ct);
        if (session is null)
        {
            return Error.NotFound("identity.session_not_found");
        }

        session.Revoke(SessionRevokeReasons.RevokedByOwner, clock.GetUtcNow());
        log.Record(SecurityEventKinds.SessionRevoked, userId, detail: session.Audience);
        await sessions.SaveAsync([session.Id], ct);
        return true;
    }

    /// <summary>
    /// New password for the signed-in member (both realms). The other sessions end (on every center for a staff
    /// password); this device stays signed in.
    /// </summary>
    public async Task<Result<bool>> ChangePasswordAsync(Guid userId, string realm, ChangePasswordCommand command, CancellationToken ct)
    {
        if (PasswordPolicy.Check(command.NewPassword) is { } weak)
        {
            return weak;
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        LockableCredential? credential = realm == Realms.Staff
            ? await db.PasswordCredentials.FirstOrDefaultAsync(c => c.UserId == userId, ct)
            : await db.LocalCredentials.FirstOrDefaultAsync(c => c.UserId == userId, ct);
        if (credential is null || user is null)
        {
            return Error.NotFound("identity.account_not_found");
        }

        var verified = await Credentials.VerifyAsync(db, hasher, clock, log, credential, user, command.CurrentPassword, user.Email, ct);
        if (!verified.IsSuccess)
        {
            return verified.Error!;
        }

        if (hasher.VerifyHashedPassword(user, credential.PasswordHash, command.NewPassword!) != PasswordVerificationResult.Failed)
        {
            return Error.Validation("identity.password_unchanged");
        }

        var now = clock.GetUtcNow();
        credential.SetPassword(hasher.HashPassword(user, command.NewPassword!), temporary: false, now);
        if (request.SessionId is { } current && await db.UserSessions.FirstOrDefaultAsync(s => s.Id == current, ct) is { } session)
        {
            session.Restamp(credential.SecurityStamp);
        }

        var revoked = await sessions.RevokeAllAsync(userId, SessionRevokeReasons.PasswordChanged, ct, except: request.SessionId);
        await PasswordResets.VoidAsync(db, userId, now, ct);
        log.Record(SecurityEventKinds.PasswordChanged, userId, user.Email, realm);
        await sessions.SaveAsync(revoked, ct);
        return true;
    }

    public async Task<Result<TwoFactorSetup>> BeginTwoFactorAsync(Guid userId, CancellationToken ct)
    {
        var (user, credential) = await StaffCredentialAsync(userId, ct);
        if (user is null || credential is null)
        {
            return Error.NotFound("identity.account_not_found");
        }

        if (credential.TwoFactorEnabled)
        {
            return Error.Conflict("identity.two_factor_already_enabled");
        }

        var setup = SecondFactor.Begin(credential, secrets, user.Email ?? user.DisplayName);
        await db.SaveChangesAsync(ct);
        return setup;
    }

    public async Task<Result<IReadOnlyList<string>>> EnableTwoFactorAsync(Guid userId, TwoFactorCodeCommand command, CancellationToken ct)
    {
        var (user, credential) = await StaffCredentialAsync(userId, ct);
        if (user is null || credential is null)
        {
            return Error.NotFound("identity.account_not_found");
        }

        if (credential.TwoFactorEnabled)
        {
            return Error.Conflict("identity.two_factor_already_enabled");
        }

        var codes = SecondFactor.Confirm(credential, secrets, command.Code, clock.GetUtcNow());
        if (codes is null)
        {
            return IdentityErrors.CodeInvalid;
        }

        log.Record(SecurityEventKinds.TwoFactorEnabled, userId, user.Email);
        await db.SaveChangesAsync(ct);
        return Result<IReadOnlyList<string>>.Success(codes);
    }

    /// <summary>Turns 2FA off with the password and a current code; refused for roles that must use it.</summary>
    public async Task<Result<bool>> DisableTwoFactorAsync(Guid userId, DisableTwoFactorCommand command, CancellationToken ct)
    {
        var (user, credential) = await StaffCredentialAsync(userId, ct);
        if (user is null || credential is null || !credential.TwoFactorEnabled)
        {
            return Error.Conflict("identity.two_factor_not_enabled");
        }

        if (policy.IsRequiredFor(await StaffLoginHandler.ActiveStaffRolesAsync(db, userId, ct)))
        {
            return Error.Conflict("identity.two_factor_required");
        }

        var verified = await Credentials.VerifyAsync(db, hasher, clock, log, credential, user, command.Password, user.Email, ct);
        if (!verified.IsSuccess)
        {
            return verified.Error!;
        }

        if (SecondFactor.Verify(credential, secrets, command.Code, command.RecoveryCode, clock.GetUtcNow()) is null)
        {
            return IdentityErrors.CodeInvalid;
        }

        credential.DisableTotp();
        log.Record(SecurityEventKinds.TwoFactorDisabled, userId, user.Email);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<Result<IReadOnlyList<string>>> RegenerateRecoveryCodesAsync(Guid userId, TwoFactorCodeCommand command, CancellationToken ct)
    {
        var (user, credential) = await StaffCredentialAsync(userId, ct);
        if (user is null || credential is null || !credential.TwoFactorEnabled)
        {
            return Error.Conflict("identity.two_factor_not_enabled");
        }

        if (SecondFactor.Verify(credential, secrets, command.Code, null, clock.GetUtcNow()) is null)
        {
            return IdentityErrors.CodeInvalid;
        }

        var codes = RecoveryCodes.Generate();
        credential.ReplaceRecoveryCodes(codes.Select(RecoveryCodes.Hash));
        log.Record(SecurityEventKinds.RecoveryCodesRegenerated, userId, user.Email);
        await db.SaveChangesAsync(ct);
        return Result<IReadOnlyList<string>>.Success(codes);
    }

    private async Task<(User?, PasswordCredential?)> StaffCredentialAsync(Guid userId, CancellationToken ct) =>
        (await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct), await db.PasswordCredentials.FirstOrDefaultAsync(c => c.UserId == userId, ct));
}

public sealed record TwoFactorCodeCommand(string? Code);

public sealed record DisableTwoFactorCommand(string? Password, string? Code, string? RecoveryCode);

public sealed record ForgotPasswordCommand(string? Email);

public sealed record ResetPasswordCommand(string? Token, string? NewPassword);

internal static class PasswordResets
{
    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim())));

    /// <summary>Every outstanding link of the user stops working (used, or the password changed another way).</summary>
    public static async Task VoidAsync(IIdentityDbContext db, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var token in await db.PasswordResetTokens.Where(t => t.UserId == userId && t.UsedAt == null).ToListAsync(ct))
        {
            token.Consume(now);
        }
    }
}

/// <summary>
/// Self-service "forgot password" for staff (they sign in with an email). The answer never tells whether the email
/// exists; links are single-use, expire after 30 minutes and at most 3 are sent per account per hour. Learners have no
/// email: the sign-in page tells them to ask their center, whose administrator resets the password.
/// </summary>
public sealed class PasswordResetHandler(
    IIdentityDbContext db, ITenantContext tenant, IPasswordHasher<User> hasher, IPasswordResetMailer mailer, SessionManager sessions,
    SecurityLog log, TimeProvider clock)
{
    public const int MaxPerHour = 3;

    public async Task RequestAsync(ForgotPasswordCommand command, string resetPageUrl, CancellationToken ct)
    {
        if (tenant.TenantId is null)
        {
            return;
        }

        var email = User.NormalizeEmail(command.Email ?? string.Empty);
        var user = email.Length == 0 ? null : await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
        var hasCredential = user is not null && await db.PasswordCredentials.AnyAsync(c => c.UserId == user.Id, ct);
        if (user is null || !hasCredential || (await StaffLoginHandler.ActiveStaffRolesAsync(db, user.Id, ct)).Count == 0)
        {
            log.Record(SecurityEventKinds.PasswordResetRequested, null, email, "unknown_account");
            await db.SaveChangesAsync(ct);
            return;
        }

        var now = clock.GetUtcNow();
        var hourAgo = now.AddHours(-1);
        if (await db.PasswordResetTokens.CountAsync(t => t.UserId == user.Id && t.CreatedAt > hourAgo, ct) >= MaxPerHour)
        {
            log.Record(SecurityEventKinds.PasswordResetRequested, user.Id, email, "throttled");
            await db.SaveChangesAsync(ct);
            return;
        }

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var row = new PasswordResetToken(user.Id, PasswordResets.Hash(token), now, null);
        db.PasswordResetTokens.Add(row);
        log.Record(SecurityEventKinds.PasswordResetRequested, user.Id, email);
        await db.SaveChangesAsync(ct);
        await mailer.SendAsync(new PasswordResetMail(row.Id, user.Id, email, user.DisplayName, user.PreferredLocale,
            $"{resetPageUrl}?token={Uri.EscapeDataString(token)}", (int)PasswordResetToken.Lifetime.TotalMinutes), ct);
    }

    public async Task<Result<bool>> ResetAsync(ResetPasswordCommand command, CancellationToken ct)
    {
        if (tenant.TenantId is null)
        {
            return IdentityErrors.TenantUnknown;
        }

        var now = clock.GetUtcNow();
        var hash = string.IsNullOrWhiteSpace(command.Token) ? string.Empty : PasswordResets.Hash(command.Token);
        var token = hash.Length == 0 ? null : await db.PasswordResetTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is null || !token.IsUsable(now))
        {
            return Error.Validation("identity.reset_link_invalid");
        }

        if (PasswordPolicy.Check(command.NewPassword) is { } weak)
        {
            return weak;
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == token.UserId, ct);
        var credential = await db.PasswordCredentials.FirstOrDefaultAsync(c => c.UserId == token.UserId, ct);
        if (user is null || credential is null)
        {
            return Error.Validation("identity.reset_link_invalid");
        }

        credential.SetPassword(hasher.HashPassword(user, command.NewPassword!), temporary: false, now);
        await PasswordResets.VoidAsync(db, user.Id, now, ct);
        token.Consume(now);
        var revoked = await sessions.RevokeAllAsync(user.Id, SessionRevokeReasons.PasswordReset, ct);
        log.Record(SecurityEventKinds.PasswordResetCompleted, user.Id, user.Email);
        await sessions.SaveAsync(revoked, ct);
        return true;
    }
}

public sealed record LearnerAccountView(Guid UserId, string DisplayName, string? Username, IReadOnlyList<string> Roles, string Status,
    bool MustChangePassword, DateTimeOffset? LockedUntil, DateTimeOffset? LastSeenAt);

public sealed record PasswordResetByAdmin(string TemporaryPassword, string? Username, string? Email);

public sealed record SecurityEventView(Guid Id, DateTimeOffset OccurredAt, string Kind, Guid? UserId, string? Subject, Guid? ActorId,
    string? ActorName, string? IpAddress, string? UserAgent, string? Detail);

/// <summary>
/// The administrator's account tools for every member of the center (staff, students, parents): lock / unlock, reset the
/// password (temporary, shown once, to change at next sign-in), remove 2FA for someone who lost their phone, sign out
/// everywhere, and the security log. A staff password and 2FA belong to the person on every center they work for, so
/// they can only be reset here when this center is the only one.
/// </summary>
public sealed class AccountAdminHandler(
    IIdentityDbContext db, IPasswordHasher<User> hasher, SessionManager sessions, SecurityLog log, IRequestContext request, TimeProvider clock)
{
    private const string PasswordAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz";
    private const string Digits = "23456789";

    public async Task<IReadOnlyList<LearnerAccountView>> ListLearnersAsync(CancellationToken ct)
    {
        var learnRoles = Roles.Learn.ToArray();
        var members = await db.Memberships.AsNoTracking()
            .Where(m => m.Roles.Any(r => learnRoles.Contains(r.Role)))
            .Select(m => new { m.UserId, m.Status, Roles = m.Roles.Select(r => r.Role).ToList() })
            .ToListAsync(ct);
        var ids = members.Select(m => m.UserId).ToList();
        var users = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, ct);
        var credentials = await db.LocalCredentials.AsNoTracking().Where(c => ids.Contains(c.UserId)).ToDictionaryAsync(c => c.UserId, ct);
        var lastSeen = await db.UserSessions.AsNoTracking().Where(s => ids.Contains(s.UserId))
            .GroupBy(s => s.UserId).Select(g => new { UserId = g.Key, At = g.Max(s => s.LastSeenAt) })
            .ToDictionaryAsync(x => x.UserId, x => x.At, ct);
        var now = clock.GetUtcNow();
        return members
            .Where(m => users.ContainsKey(m.UserId))
            .Select(m =>
            {
                credentials.TryGetValue(m.UserId, out var credential);
                return new LearnerAccountView(m.UserId, users[m.UserId].DisplayName, credential?.Username,
                    m.Roles.Where(Roles.Learn.Contains).Order().ToList(), m.Status.ToString(), credential?.MustChangePassword ?? false,
                    credential?.IsLocked(now) == true ? credential.LockedUntil : null, lastSeen.TryGetValue(m.UserId, out var at) ? at : null);
            })
            .OrderBy(a => a.Status != nameof(MembershipStatus.Active)).ThenBy(a => a.DisplayName, StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>Locks the member out of this center: no sign-in, current sessions end. The last active administrator stays.</summary>
    public async Task<Result<bool>> SuspendAsync(Guid userId, CancellationToken ct)
    {
        if (userId == request.ActorId)
        {
            return Error.Conflict("identity.cannot_suspend_self");
        }

        var membership = await db.Memberships.Include(m => m.Roles).FirstOrDefaultAsync(m => m.UserId == userId, ct);
        if (membership is null)
        {
            return Error.NotFound("identity.account_not_found");
        }

        if (IsActiveAdmin(membership) && !await AnotherActiveAdminAsync(membership.Id, ct))
        {
            return Error.Conflict("identity.last_admin");
        }

        if (membership.Status == MembershipStatus.Suspended)
        {
            return true;
        }

        membership.Suspend();
        var revoked = await sessions.RevokeAllAsync(userId, SessionRevokeReasons.AccountSuspended, ct);
        log.Record(SecurityEventKinds.AccountSuspended, userId, await NameAsync(userId, ct));
        await sessions.SaveAsync(revoked, ct);
        return true;
    }

    public async Task<Result<bool>> ActivateAsync(Guid userId, CancellationToken ct)
    {
        var membership = await db.Memberships.FirstOrDefaultAsync(m => m.UserId == userId, ct);
        if (membership is null)
        {
            return Error.NotFound("identity.account_not_found");
        }

        if (membership.Status != MembershipStatus.Active)
        {
            membership.Activate();
            log.Record(SecurityEventKinds.AccountReactivated, userId, await NameAsync(userId, ct));
            await db.SaveChangesAsync(ct);
        }

        return true;
    }

    public async Task<Result<PasswordResetByAdmin>> ResetPasswordAsync(Guid userId, CancellationToken ct)
    {
        if (userId == request.ActorId)
        {
            return Error.Conflict("identity.cannot_reset_self");
        }

        var (user, credential, error) = await CredentialOfMemberAsync(userId, ct);
        if (error is not null)
        {
            return error;
        }

        var now = clock.GetUtcNow();
        var temporary = TemporaryPassword();
        credential!.SetPassword(hasher.HashPassword(user!, temporary), temporary: true, now);
        await PasswordResets.VoidAsync(db, userId, now, ct);
        var revoked = await sessions.RevokeAllAsync(userId, SessionRevokeReasons.PasswordReset, ct);
        log.Record(SecurityEventKinds.PasswordResetByAdmin, userId, user!.Email ?? (credential as LocalCredential)?.Username);
        await sessions.SaveAsync(revoked, ct);
        return new PasswordResetByAdmin(temporary, (credential as LocalCredential)?.Username, user.Email);
    }

    /// <summary>For a staff member who lost their authenticator: 2FA off (they enroll again at next sign-in if their role needs it).</summary>
    public async Task<Result<bool>> ResetTwoFactorAsync(Guid userId, CancellationToken ct)
    {
        var (user, credential, error) = await CredentialOfMemberAsync(userId, ct, staffOnly: true);
        if (error is not null)
        {
            return error;
        }

        var staff = (PasswordCredential)credential!;
        if (!staff.TwoFactorEnabled && staff.PendingTotpSecret is null)
        {
            return Error.Conflict("identity.two_factor_not_enabled");
        }

        staff.DisableTotp();
        var revoked = await sessions.RevokeAllAsync(userId, SessionRevokeReasons.TwoFactorReset, ct);
        log.Record(SecurityEventKinds.TwoFactorResetByAdmin, userId, user!.Email);
        db.LogChange(nameof(PasswordCredential), staff.Id, userId, "Updated", [("twoFactor", "on", "off")]);
        await sessions.SaveAsync(revoked, ct);
        return true;
    }

    public async Task<Result<int>> SignOutEverywhereAsync(Guid userId, CancellationToken ct)
    {
        if (!await db.Memberships.AnyAsync(m => m.UserId == userId, ct))
        {
            return Error.NotFound("identity.account_not_found");
        }

        var revoked = await sessions.RevokeAllAsync(userId, SessionRevokeReasons.SignedOutByAdmin, ct);
        log.Record(SecurityEventKinds.SignedOutEverywhere, userId, await NameAsync(userId, ct));
        await sessions.SaveAsync(revoked, ct);
        return revoked.Count;
    }

    public async Task<IReadOnlyList<SecurityEventView>> EventsAsync(Guid? userId, string? kind, int take, CancellationToken ct) =>
        await db.SecurityEvents.AsNoTracking()
            .Where(e => (userId == null || e.UserId == userId) && (kind == null || e.Kind == kind))
            .OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id)
            .Take(Math.Clamp(take, 1, 500))
            .Select(e => new SecurityEventView(e.Id, e.OccurredAt, e.Kind, e.UserId, e.Subject, e.ActorId, e.ActorName, e.IpAddress, e.UserAgent, e.Detail))
            .ToListAsync(ct);

    /// <summary>The credential an administrator of this center may reset: the center's own learner account, or a staff password used here only.</summary>
    private async Task<(User?, LockableCredential?, Error?)> CredentialOfMemberAsync(Guid userId, CancellationToken ct, bool staffOnly = false)
    {
        var member = await db.Memberships.AsNoTracking().AnyAsync(m => m.UserId == userId, ct);
        var user = member ? await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct) : null;
        if (user is null)
        {
            return (null, null, Error.NotFound("identity.account_not_found"));
        }

        if (!staffOnly && await db.LocalCredentials.FirstOrDefaultAsync(c => c.UserId == userId, ct) is { } local)
        {
            return (user, local, null);
        }

        var staff = await db.PasswordCredentials.FirstOrDefaultAsync(c => c.UserId == userId, ct);
        if (staff is null)
        {
            return (null, null, Error.NotFound("identity.account_not_found"));
        }

        if (await db.CountCentersOfUserAsync(userId, ct) != 1)
        {
            return (null, null, Error.Conflict("identity.account_shared_with_other_centers"));
        }

        return (user, staff, null);
    }

    private async Task<string?> NameAsync(Guid userId, CancellationToken ct) =>
        await db.Users.Where(u => u.Id == userId).Select(u => u.Email ?? u.DisplayName).FirstOrDefaultAsync(ct);

    private static bool IsActiveAdmin(Membership membership) =>
        membership.Status == MembershipStatus.Active &&
        membership.Roles.Any(r => r.Role == Roles.TenantAdmin && r.ScopeType == ScopeType.Tenant);

    private Task<bool> AnotherActiveAdminAsync(Guid membershipId, CancellationToken ct) =>
        db.Memberships.AnyAsync(m => m.Id != membershipId && m.Status == MembershipStatus.Active &&
                                     m.Roles.Any(r => r.Role == Roles.TenantAdmin && r.ScopeType == ScopeType.Tenant), ct);

    /// <summary>Ten characters, letters and at least two digits (meets the password policy if kept by mistake).</summary>
    internal static string TemporaryPassword()
    {
        var chars = (RandomNumberGenerator.GetString(PasswordAlphabet, 8) + RandomNumberGenerator.GetString(Digits, 2)).ToCharArray();
        RandomNumberGenerator.Shuffle(chars.AsSpan());
        return new string(chars);
    }
}
