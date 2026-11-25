using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Konnyaku.Application.Tenancy;
using Konnyaku.SharedKernel;

namespace Konnyaku.Crm.Application;

/// <summary>Configuration section <c>Captcha</c>. <see cref="Key"/> is a secret of at least 32 bytes, shared by every API replica.</summary>
public sealed class CaptchaOptions
{
    public const string Section = "Captcha";

    /// <summary>Off only for tests and local seeding; production keeps it on.</summary>
    public bool Enabled { get; set; } = true;

    public string? Key { get; set; }

    /// <summary>Upper bound of the secret number: the browser tries on average half of it (~0.1-0.5 s on a phone at 100 000).</summary>
    public int MaxNumber { get; set; } = 100_000;

    /// <summary>A challenge must be solved and sent within this time (the visitor may take a while to fill the form).</summary>
    public int ExpiresMinutes { get; set; } = 30;
}

/// <summary>
/// Proof-of-work challenge (ALTCHA scheme): find <c>n</c> in [0, MaxNumber] with SHA-256(salt + n) = challenge.
/// Self-hosted, stateless until redeemed, nothing leaves the server.
/// </summary>
public sealed record CaptchaChallenge(string Algorithm, string Challenge, int MaxNumber, string Salt, string Signature);

/// <summary>The challenge as received plus the number the browser found.</summary>
public sealed record CaptchaSolution(string? Salt, string? Challenge, string? Signature, long Number);

/// <summary>A verified solution, still to be redeemed (stored) together with the form it protects.</summary>
public sealed record CaptchaTicket(string Challenge, DateTimeOffset ExpiresAt);

/// <summary>
/// Issues and checks signed challenges. The HMAC covers tenant, salt (random + expiry) and challenge, so a challenge
/// cannot be forged, extended, or reused on another center. Single use is enforced when the ticket is redeemed.
/// </summary>
public sealed class CaptchaService(CaptchaOptions options, ITenantContext tenant, TimeProvider clock)
{
    public const string Algorithm = "SHA-256";

    public bool Enabled => options.Enabled;

    public Result<CaptchaChallenge> Issue()
    {
        if (tenant.TenantId is not { } tenantId)
        {
            return Error.Validation("crm.tenant_unknown");
        }

        var expires = clock.GetUtcNow().AddMinutes(options.ExpiresMinutes).ToUnixTimeSeconds();
        var salt = $"{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12))}.{expires}";
        var number = RandomNumberGenerator.GetInt32(0, options.MaxNumber + 1);
        var challenge = Hash(salt, number);
        return new CaptchaChallenge(Algorithm, challenge, options.MaxNumber, salt, Sign(tenantId, salt, challenge));
    }

    public Result<CaptchaTicket> Verify(CaptchaSolution? solution)
    {
        if (tenant.TenantId is not { } tenantId)
        {
            return Error.Validation("crm.tenant_unknown");
        }

        if (solution is not { Salt.Length: > 0, Challenge.Length: > 0, Signature.Length: > 0 })
        {
            return Error.Validation("crm.captcha_required");
        }

        var expected = Encoding.ASCII.GetBytes(Sign(tenantId, solution.Salt, solution.Challenge));
        var given = Encoding.ASCII.GetBytes(solution.Signature.ToLowerInvariant());
        if (!CryptographicOperations.FixedTimeEquals(expected, given) || !TryReadExpiry(solution.Salt, out var expiresAt))
        {
            return Error.Validation("crm.captcha_invalid");
        }

        if (expiresAt <= clock.GetUtcNow())
        {
            return Error.Validation("crm.captcha_expired");
        }

        if (solution.Number < 0 || solution.Number > options.MaxNumber || Hash(solution.Salt, solution.Number) != solution.Challenge)
        {
            return Error.Validation("crm.captcha_invalid");
        }

        return new CaptchaTicket(solution.Challenge, expiresAt);
    }

    public static string Hash(string salt, long number) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(salt + number.ToString(CultureInfo.InvariantCulture))));

    private string Sign(Guid tenantId, string salt, string challenge)
    {
        if (options.Key is not { } key || Encoding.UTF8.GetByteCount(key) < 32)
        {
            throw new InvalidOperationException("Captcha:Key must be configured with at least 32 bytes (or set Captcha:Enabled=false).");
        }

        return Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes($"{tenantId:N}|{salt}|{challenge}")));
    }

    private static bool TryReadExpiry(string salt, out DateTimeOffset expiresAt)
    {
        expiresAt = default;
        var dot = salt.LastIndexOf('.');
        if (dot < 0 || !long.TryParse(salt.AsSpan(dot + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return false;
        }

        try
        {
            expiresAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}
