using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Konnyaku.Infrastructure.Auth;

/// <summary>
/// Third sign-in realm, for Konnyaku's own operators (platform console). Its tokens carry no tenant and the
/// <c>platform</c> audience, so they are refused by every staff/learn endpoint, and staff tokens are refused here.
/// Sign-in takes two steps: the password step returns a short-lived challenge token (audience <c>platform-mfa</c>,
/// never accepted as a bearer token) that the TOTP step exchanges for the access token.
/// </summary>
public static class PlatformAuth
{
    public const string Audience = "platform";
    public const string ChallengeAudience = "platform-mfa";
    public const string Policy = "platform";
    public const string OperatorRole = "PlatformOperator";

    public static readonly TimeSpan AccessLifetime = TimeSpan.FromHours(8);
    public static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(5);

    public static IServiceCollection AddPlatformAuth(this IServiceCollection services)
    {
        services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            var audiences = options.TokenValidationParameters.ValidAudiences ?? [];
            options.TokenValidationParameters.ValidAudiences = audiences.Append(Audience).Distinct().ToArray();
        });
        services.Configure<AuthorizationOptions>(options => options.AddPolicy(Policy, p => p
            .RequireAuthenticatedUser()
            .RequireClaim(KonnyakuClaims.Audience, Audience)
            .RequireRole(OperatorRole)));
        services.AddSingleton<IPlatformTokenService, PlatformTokenService>();
        return services;
    }
}

public interface IPlatformTokenService
{
    IssuedToken IssueAccess(Guid operatorId, string displayName);

    (string Token, DateTimeOffset ExpiresAt) IssueChallenge(Guid operatorId);

    /// <summary>Operator id of a valid, unexpired challenge; null otherwise.</summary>
    Task<Guid?> ValidateChallengeAsync(string? challenge);
}

internal sealed class PlatformTokenService(IConfiguration configuration, TimeProvider clock) : IPlatformTokenService
{
    private static readonly JsonWebTokenHandler Handler = new();

    public IssuedToken IssueAccess(Guid operatorId, string displayName)
    {
        var expires = clock.GetUtcNow().Add(PlatformAuth.AccessLifetime);
        var token = Create(PlatformAuth.Audience, expires, new Dictionary<string, object>
        {
            [KonnyakuClaims.Subject] = operatorId.ToString(),
            [KonnyakuClaims.Name] = displayName,
            [KonnyakuClaims.Role] = new[] { PlatformAuth.OperatorRole },
        });
        return new IssuedToken(token, PlatformAuth.Audience, expires);
    }

    public (string Token, DateTimeOffset ExpiresAt) IssueChallenge(Guid operatorId)
    {
        var expires = clock.GetUtcNow().Add(PlatformAuth.ChallengeLifetime);
        return (Create(PlatformAuth.ChallengeAudience, expires, new Dictionary<string, object>
        {
            [KonnyakuClaims.Subject] = operatorId.ToString(),
        }), expires);
    }

    public async Task<Guid?> ValidateChallengeAsync(string? challenge)
    {
        if (string.IsNullOrWhiteSpace(challenge))
        {
            return null;
        }

        var result = await Handler.ValidateTokenAsync(challenge, new TokenValidationParameters
        {
            ValidIssuer = KonnyakuAuthExtensions.Issuer,
            ValidAudience = PlatformAuth.ChallengeAudience,
            IssuerSigningKey = KonnyakuAuthExtensions.SigningKey(configuration),
            ClockSkew = TimeSpan.FromSeconds(30),
        });
        return result.IsValid && result.Claims.TryGetValue(KonnyakuClaims.Subject, out var sub) && Guid.TryParse(sub?.ToString(), out var id)
            ? id
            : null;
    }

    private string Create(string audience, DateTimeOffset expires, Dictionary<string, object> claims) =>
        Handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = KonnyakuAuthExtensions.Issuer,
            Audience = audience,
            Claims = claims,
            IssuedAt = clock.GetUtcNow().UtcDateTime,
            NotBefore = clock.GetUtcNow().UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(KonnyakuAuthExtensions.SigningKey(configuration), SecurityAlgorithms.HmacSha256),
        });
}
