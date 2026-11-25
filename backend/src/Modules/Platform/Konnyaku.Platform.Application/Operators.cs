using System.Security.Cryptography;
using Konnyaku.Platform.Domain;
using Konnyaku.SharedKernel;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Konnyaku.Platform.Application;

/// <summary>Encrypts secrets kept in the database (TOTP keys), so a database dump alone cannot generate codes.</summary>
public interface ISecretProtector
{
    string Protect(byte[] secret);

    byte[] Unprotect(string protectedSecret);
}

internal static class OperatorErrors
{
    // One code for unknown email, wrong password and disabled account: never reveal whether an operator exists.
    public static readonly Error InvalidCredentials = Error.Validation("platform.invalid_credentials");
    public static readonly Error Locked = Error.Validation("platform.account_locked");
    public static readonly Error InvalidCode = Error.Validation("platform.invalid_code");

    public static Error PasswordTooShort => Error.Validation(
        "platform.password_too_short", new Dictionary<string, object?> { ["min"] = PlatformOperator.MinPasswordLength });
}

public sealed record OperatorView(
    Guid Id, string Email, string DisplayName, string Status, bool TotpEnrolled, bool MustChangePassword,
    int RecoveryCodesLeft, DateTimeOffset CreatedAt, DateTimeOffset? LastSignInAt);

public sealed record BootstrapOperatorCommand(string Email, string DisplayName, string Password, string? TotpSecret);

/// <param name="RecoveryCodes">Only when a TOTP secret was supplied (enrolled at once); otherwise enrollment happens at first sign-in.</param>
public sealed record OperatorBootstrapped(Guid Id, string Email, IReadOnlyList<string>? RecoveryCodes);

public sealed record InviteOperatorCommand(string Email, string DisplayName);

public sealed record OperatorInvited(Guid Id, string Email, string TemporaryPassword);

public sealed record OperatorSignInCommand(string Email, string Password);

/// <summary>First step passed. The second step needs a TOTP code; without an enrolled app, scan <see cref="OtpAuthUri"/> first.</summary>
public sealed record PasswordAccepted(Guid OperatorId, bool EnrollmentRequired, string? Secret, string? OtpAuthUri);

public sealed record SecondFactorCommand(string? Code, string? RecoveryCode);

/// <param name="RecoveryCodes">New codes, shown once, when this sign-in completed TOTP enrollment.</param>
public sealed record OperatorSignedIn(
    Guid OperatorId, string Email, string DisplayName, bool MustChangePassword, IReadOnlyList<string>? RecoveryCodes, int RecoveryCodesLeft);

public sealed record ChangeOperatorPasswordCommand(string CurrentPassword, string NewPassword);

public sealed class OperatorHandlers(
    IPlatformDbContext db, IPasswordHasher<PlatformOperator> hasher, ISecretProtector secrets, AuditLog audit, TimeProvider clock)
{
    public const string TotpIssuer = "Konnyaku Platform";
    private const string PasswordAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";

    /// <summary>First operator of a fresh installation, by automation (platform key). Refused once any operator exists.</summary>
    public async Task<Result<OperatorBootstrapped>> BootstrapAsync(BootstrapOperatorCommand command, PlatformActor actor, CancellationToken cancellationToken)
    {
        if (await db.Operators.AnyAsync(cancellationToken))
        {
            return Error.Conflict("platform.bootstrap_done");
        }

        if ((command.Password ?? string.Empty).Length < PlatformOperator.MinPasswordLength)
        {
            return OperatorErrors.PasswordTooShort;
        }

        byte[]? secret = null;
        if (!string.IsNullOrWhiteSpace(command.TotpSecret) && (!Base32.TryDecode(command.TotpSecret, out secret) || secret.Length < 10))
        {
            return Error.Validation("platform.totp_secret_invalid");
        }

        var created = PlatformOperator.Create(command.Email, command.DisplayName, invitedBy: null, clock.GetUtcNow());
        if (!created.IsSuccess)
        {
            return created.Error!;
        }

        var account = created.Value;
        account.ChangePassword(hasher.HashPassword(account, command.Password!));
        IReadOnlyList<string>? recoveryCodes = null;
        if (secret is not null)
        {
            recoveryCodes = RecoveryCodes.Generate();
            account.EnrollTotp(secrets.Protect(secret), recoveryCodes.Select(RecoveryCodes.Hash).ToList());
        }

        db.Operators.Add(account);
        audit.Record(actor, AuditActions.OperatorBootstrap, null, new Dictionary<string, string?> { ["email"] = account.Email });
        await db.SaveChangesAsync(cancellationToken);
        return new OperatorBootstrapped(account.Id, account.Email, recoveryCodes);
    }

    /// <summary>An operator adds another one. The temporary password is shown once; TOTP is enrolled at first sign-in.</summary>
    public async Task<Result<OperatorInvited>> InviteAsync(InviteOperatorCommand command, PlatformActor actor, CancellationToken cancellationToken)
    {
        var email = PlatformOperator.NormalizeEmail(command.Email);
        if (await db.Operators.AnyAsync(o => o.Email == email, cancellationToken))
        {
            return Error.Conflict("platform.operator_email_taken");
        }

        var created = PlatformOperator.Create(email, command.DisplayName, actor.OperatorId, clock.GetUtcNow());
        if (!created.IsSuccess)
        {
            return created.Error!;
        }

        var account = created.Value;
        var temporaryPassword = RandomNumberGenerator.GetString(PasswordAlphabet, 16);
        account.ChangePassword(hasher.HashPassword(account, temporaryPassword));
        account.RequirePasswordChange();
        db.Operators.Add(account);
        audit.Record(actor, AuditActions.OperatorInvite, null, new Dictionary<string, string?> { ["email"] = account.Email });
        await db.SaveChangesAsync(cancellationToken);
        return new OperatorInvited(account.Id, account.Email, temporaryPassword);
    }

    public async Task<IReadOnlyList<OperatorView>> ListAsync(CancellationToken cancellationToken) =>
        (await db.Operators.AsNoTracking().OrderBy(o => o.CreatedAt).ToListAsync(cancellationToken)).Select(ToView).ToList();

    public async Task<OperatorView?> GetAsync(Guid operatorId, CancellationToken cancellationToken) =>
        await db.Operators.AsNoTracking().FirstOrDefaultAsync(o => o.Id == operatorId, cancellationToken) is { } account ? ToView(account) : null;

    public async Task<Result<OperatorView>> SetStatusAsync(Guid operatorId, bool active, PlatformActor actor, CancellationToken cancellationToken)
    {
        var account = await db.Operators.FirstOrDefaultAsync(o => o.Id == operatorId, cancellationToken);
        if (account is null)
        {
            return Error.NotFound("platform.operator_not_found");
        }

        if (!active && operatorId == actor.OperatorId)
        {
            return Error.Validation("platform.operator_cannot_disable_self");
        }

        if (active)
        {
            account.Enable();
        }
        else
        {
            account.Disable();
        }

        audit.Record(actor, active ? AuditActions.OperatorEnable : AuditActions.OperatorDisable, null,
            new Dictionary<string, string?> { ["email"] = account.Email });
        await db.SaveChangesAsync(cancellationToken);
        return ToView(account);
    }

    /// <summary>Step 1 of sign-in: email + password. Wrong attempts count toward the lockout shared with step 2.</summary>
    public async Task<Result<PasswordAccepted>> SignInAsync(OperatorSignInCommand command, string? ipAddress, CancellationToken cancellationToken)
    {
        var email = PlatformOperator.NormalizeEmail(command.Email);
        var account = await db.Operators.FirstOrDefaultAsync(o => o.Email == email, cancellationToken);
        if (account is null || account.Status != OperatorStatus.Active)
        {
            return OperatorErrors.InvalidCredentials;
        }

        var now = clock.GetUtcNow();
        if (account.IsLocked(now))
        {
            return OperatorErrors.Locked;
        }

        if (hasher.VerifyHashedPassword(account, account.PasswordHash, command.Password ?? string.Empty) == PasswordVerificationResult.Failed)
        {
            return await FailAsync(account, OperatorErrors.InvalidCredentials, ipAddress, cancellationToken);
        }

        if (account.TotpEnrolled)
        {
            return new PasswordAccepted(account.Id, EnrollmentRequired: false, null, null);
        }

        // Not enrolled yet (invited operator, or bootstrap without a secret): a new secret for each attempt until confirmed.
        var secret = Totp.NewSecret();
        account.StartTotpEnrollment(secrets.Protect(secret));
        await db.SaveChangesAsync(cancellationToken);
        var base32 = Base32.Encode(secret);
        return new PasswordAccepted(account.Id, EnrollmentRequired: true, base32, Totp.OtpAuthUri(TotpIssuer, account.Email, base32));
    }

    /// <summary>Step 2 of sign-in: a TOTP code (or a single-use recovery code). Completes enrollment on first use.</summary>
    public async Task<Result<OperatorSignedIn>> VerifySecondFactorAsync(
        Guid operatorId, SecondFactorCommand command, string? ipAddress, CancellationToken cancellationToken)
    {
        var account = await db.Operators.FirstOrDefaultAsync(o => o.Id == operatorId, cancellationToken);
        if (account is null || account.Status != OperatorStatus.Active)
        {
            return OperatorErrors.InvalidCredentials;
        }

        var now = clock.GetUtcNow();
        if (account.IsLocked(now))
        {
            return OperatorErrors.Locked;
        }

        var actor = new PlatformActor(account.Id, account.Email, ipAddress);
        IReadOnlyList<string>? newRecoveryCodes = null;
        if (!account.TotpEnrolled)
        {
            if (account.PendingTotpSecret is not { } pending || Totp.Match(secrets.Unprotect(pending), command.Code, now) is not { } step)
            {
                return await FailAsync(account, OperatorErrors.InvalidCode, ipAddress, cancellationToken);
            }

            newRecoveryCodes = RecoveryCodes.Generate();
            account.CompleteTotpEnrollment(step, newRecoveryCodes.Select(RecoveryCodes.Hash).ToList());
            audit.Record(actor, AuditActions.OperatorTotpEnrolled, null);
        }
        else if (!string.IsNullOrWhiteSpace(command.RecoveryCode))
        {
            if (!account.UseRecoveryCode(RecoveryCodes.Hash(command.RecoveryCode)))
            {
                return await FailAsync(account, OperatorErrors.InvalidCode, ipAddress, cancellationToken);
            }

            audit.Record(actor, AuditActions.OperatorRecoveryCodeUsed, null,
                new Dictionary<string, string?> { ["left"] = account.RecoveryCodeHashes.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }
        else if (Totp.Match(secrets.Unprotect(account.TotpSecret!), command.Code, now) is not { } step || !account.AcceptTotpStep(step))
        {
            return await FailAsync(account, OperatorErrors.InvalidCode, ipAddress, cancellationToken);
        }

        account.RegisterSignIn(now);
        audit.Record(actor, AuditActions.OperatorSignIn, null);
        await db.SaveChangesAsync(cancellationToken);
        return new OperatorSignedIn(
            account.Id, account.Email, account.DisplayName, account.MustChangePassword, newRecoveryCodes, account.RecoveryCodeHashes.Count);
    }

    public async Task<Result<bool>> ChangePasswordAsync(
        Guid operatorId, ChangeOperatorPasswordCommand command, PlatformActor actor, CancellationToken cancellationToken)
    {
        var account = await db.Operators.FirstOrDefaultAsync(o => o.Id == operatorId, cancellationToken);
        if (account is null)
        {
            return Error.NotFound("platform.operator_not_found");
        }

        if ((command.NewPassword ?? string.Empty).Length < PlatformOperator.MinPasswordLength)
        {
            return OperatorErrors.PasswordTooShort;
        }

        var now = clock.GetUtcNow();
        if (account.IsLocked(now))
        {
            return OperatorErrors.Locked;
        }

        if (hasher.VerifyHashedPassword(account, account.PasswordHash, command.CurrentPassword ?? string.Empty) == PasswordVerificationResult.Failed)
        {
            return await FailAsync(account, Error.Validation("platform.current_password_wrong"), actor.IpAddress, cancellationToken);
        }

        if (hasher.VerifyHashedPassword(account, account.PasswordHash, command.NewPassword!) != PasswordVerificationResult.Failed)
        {
            return Error.Validation("platform.password_unchanged");
        }

        account.ChangePassword(hasher.HashPassword(account, command.NewPassword!));
        audit.Record(actor, AuditActions.OperatorPasswordChanged, null);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<Error> FailAsync(PlatformOperator account, Error error, string? ipAddress, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        account.RegisterFailure(now);
        if (account.IsLocked(now))
        {
            audit.Record(new PlatformActor(account.Id, account.Email, ipAddress), AuditActions.OperatorLocked, null);
            error = OperatorErrors.Locked;
        }

        await db.SaveChangesAsync(cancellationToken);
        return error;
    }

    private static OperatorView ToView(PlatformOperator o) => new(
        o.Id, o.Email, o.DisplayName, o.Status.ToString(), o.TotpEnrolled, o.MustChangePassword, o.RecoveryCodeHashes.Count, o.CreatedAt, o.LastSignInAt);
}
