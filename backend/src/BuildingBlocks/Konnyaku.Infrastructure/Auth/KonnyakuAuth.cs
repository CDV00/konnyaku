using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Konnyaku.Application.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Konnyaku.Infrastructure.Auth;

/// <summary>
/// Two sign-in realms with separate tokens: <c>learn</c> (students, parents) and <c>staff</c> (teachers, employees).
/// Every endpoint group declares which audience it accepts, so a learner token never reaches staff endpoints.
/// </summary>
public static class Audiences
{
    public const string Staff = "staff";
    public const string Learn = "learn";
}

public static class Policies
{
    public const string Staff = "staff";
    public const string Learn = "learn";
    public const string TenantAdmin = "staff:tenant-admin";
}

public static class KonnyakuClaims
{
    public const string Subject = "sub";
    public const string Tenant = "tid";
    public const string Role = "role";
    public const string Name = "name";
    public const string Audience = "aud";

    /// <summary>Server-side sign-in session the token belongs to (see <see cref="ISessionValidator"/>).</summary>
    public const string Session = "sid";

    public static Guid? TenantId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(Tenant), out var id) ? id : null;

    public static Guid? UserId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(Subject), out var id) ? id : null;

    public static Guid? SessionId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(Session), out var id) ? id : null;
}

public static class KonnyakuAuthExtensions
{
    public const string Issuer = "konnyaku";

    public static IServiceCollection AddKonnyakuAuth(this IServiceCollection services)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IConfiguration>((options, configuration) =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = Issuer,
                    ValidAudiences = [Audiences.Staff, Audiences.Learn],
                    IssuerSigningKey = SigningKey(configuration),
                    NameClaimType = KonnyakuClaims.Name,
                    RoleClaimType = KonnyakuClaims.Role,
                    ClockSkew = TimeSpan.FromMinutes(1),
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Staff, p => p.RequireAuthenticatedUser().RequireClaim(KonnyakuClaims.Audience, Audiences.Staff))
            .AddPolicy(Policies.Learn, p => p.RequireAuthenticatedUser().RequireClaim(KonnyakuClaims.Audience, Audiences.Learn))
            .AddPolicy(Policies.TenantAdmin, p => p.RequireAuthenticatedUser()
                .RequireClaim(KonnyakuClaims.Audience, Audiences.Staff)
                .RequireRole("TenantAdmin"));

        services.AddSingleton<ITokenIssuer, JwtTokenIssuer>();
        return services;
    }

    internal static SymmetricSecurityKey SigningKey(IConfiguration configuration)
    {
        var key = configuration["Auth:SigningKey"];
        if (string.IsNullOrEmpty(key) || Encoding.UTF8.GetByteCount(key) < 32)
        {
            throw new InvalidOperationException("Auth:SigningKey must be configured with at least 32 bytes.");
        }

        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key));
    }

    /// <summary>
    /// Staff endpoint that changes data: the member must hold <paramref name="permission"/> (in any scope, read live from
    /// their role assignments). Handlers then check the branch of the record with <see cref="IAccessControl"/>.
    /// </summary>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, params string[] anyOf) where TBuilder : IEndpointConventionBuilder
    {
        builder.RequireAuthorization(Policies.Staff);
        builder.AddEndpointFilter(async (context, next) =>
        {
            var access = context.HttpContext.RequestServices.GetRequiredService<IAccessControl>();
            foreach (var permission in anyOf)
            {
                if (await access.HasAsync(permission, context.HttpContext.RequestAborted))
                {
                    return await next(context);
                }
            }

            return Results.Json(new { code = "access.forbidden", @params = new Dictionary<string, object?>() }, statusCode: StatusCodes.Status403Forbidden);
        });
        return builder;
    }

    /// <summary>Platform operations (creating tenants) require the <c>X-Platform-Key</c> header.</summary>
    public static RouteHandlerBuilder RequirePlatformKey(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            var configuration = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
            var expected = configuration["Platform:AdminKey"];
            var provided = context.HttpContext.Request.Headers["X-Platform-Key"].ToString();
            var ok = !string.IsNullOrEmpty(expected) && CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(provided));
            return ok ? await next(context) : Results.Unauthorized();
        });
}

public sealed record IssuedToken(string AccessToken, string Audience, DateTimeOffset ExpiresAt);

/// <summary>What a staff sign-in still needs after the password step: the TOTP code, or enrolling 2FA first.</summary>
public static class ChallengePurposes
{
    public const string Verify = "verify";
    public const string Enroll = "enroll";
}

/// <param name="Stamp">Security stamp of the credential when the password was checked: a password change voids the challenge.</param>
public sealed record SignInChallenge(Guid UserId, Guid TenantId, string Purpose, Guid Stamp);

public interface ITokenIssuer
{
    IssuedToken Issue(Guid userId, Guid tenantId, string audience, string displayName, IReadOnlyCollection<string> roles, Guid sessionId,
        DateTimeOffset expiresAt);

    /// <summary>Short-lived token between the password step and the second factor; never accepted as a bearer token.</summary>
    string IssueChallenge(SignInChallenge challenge, DateTimeOffset expiresAt);

    Task<SignInChallenge?> ValidateChallengeAsync(string? token);
}

internal sealed class JwtTokenIssuer(IConfiguration configuration) : ITokenIssuer
{
    public const string ChallengeAudience = "staff-mfa";
    private static readonly JsonWebTokenHandler Handler = new();

    public IssuedToken Issue(Guid userId, Guid tenantId, string audience, string displayName, IReadOnlyCollection<string> roles, Guid sessionId,
        DateTimeOffset expiresAt)
    {
        var claims = new Dictionary<string, object>
        {
            [KonnyakuClaims.Subject] = userId.ToString(),
            [KonnyakuClaims.Tenant] = tenantId.ToString(),
            [KonnyakuClaims.Name] = displayName,
            [KonnyakuClaims.Role] = roles.ToArray(),
            [KonnyakuClaims.Session] = sessionId.ToString(),
        };
        return new IssuedToken(Create(audience, claims, expiresAt), audience, expiresAt);
    }

    public string IssueChallenge(SignInChallenge challenge, DateTimeOffset expiresAt) =>
        Create(ChallengeAudience, new Dictionary<string, object>
        {
            [KonnyakuClaims.Subject] = challenge.UserId.ToString(),
            [KonnyakuClaims.Tenant] = challenge.TenantId.ToString(),
            ["purpose"] = challenge.Purpose,
            ["stamp"] = challenge.Stamp.ToString(),
        }, expiresAt);

    public async Task<SignInChallenge?> ValidateChallengeAsync(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var result = await Handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = KonnyakuAuthExtensions.Issuer,
            ValidAudience = ChallengeAudience,
            IssuerSigningKey = KonnyakuAuthExtensions.SigningKey(configuration),
            ClockSkew = TimeSpan.FromSeconds(30),
        });
        string? Claim(string name) => result.Claims.TryGetValue(name, out var value) ? value?.ToString() : null;
        return result.IsValid
               && Guid.TryParse(Claim(KonnyakuClaims.Subject), out var userId)
               && Guid.TryParse(Claim(KonnyakuClaims.Tenant), out var tenantId)
               && Guid.TryParse(Claim("stamp"), out var stamp)
               && Claim("purpose") is ChallengePurposes.Verify or ChallengePurposes.Enroll
            ? new SignInChallenge(userId, tenantId, Claim("purpose")!, stamp)
            : null;
    }

    private string Create(string audience, Dictionary<string, object> claims, DateTimeOffset expiresAt) =>
        Handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = KonnyakuAuthExtensions.Issuer,
            Audience = audience,
            Claims = claims,
            Expires = expiresAt.UtcDateTime,
            SigningCredentials = new SigningCredentials(KonnyakuAuthExtensions.SigningKey(configuration), SecurityAlgorithms.HmacSha256),
        });
}

/// <summary>
/// Runs after tenant resolution: a staff or learner token whose sign-in session was revoked, has expired or names no
/// session is treated as anonymous, so protected endpoints answer 401 and the web app sends the person to the login page.
/// </summary>
public sealed class SessionValidationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var user = context.User;
        var audience = user.FindFirstValue(KonnyakuClaims.Audience);
        if (user.Identity?.IsAuthenticated == true && audience is Audiences.Staff or Audiences.Learn)
        {
            var validator = context.RequestServices.GetService<ISessionValidator>();
            var active = validator is not null
                         && user.SessionId() is { } sessionId
                         && user.UserId() is { } userId
                         && await validator.IsActiveAsync(sessionId, userId, audience, context.RequestAborted);
            if (!active)
            {
                context.User = new ClaimsPrincipal(new ClaimsIdentity());
            }
        }

        await next(context);
    }
}
