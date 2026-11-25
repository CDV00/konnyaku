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

    public static Guid? TenantId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(Tenant), out var id) ? id : null;

    public static Guid? UserId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(Subject), out var id) ? id : null;
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

public interface ITokenIssuer
{
    IssuedToken Issue(Guid userId, Guid tenantId, string audience, string displayName, IReadOnlyCollection<string> roles);
}

internal sealed class JwtTokenIssuer(IConfiguration configuration) : ITokenIssuer
{
    private static readonly JsonWebTokenHandler Handler = new();

    public IssuedToken Issue(Guid userId, Guid tenantId, string audience, string displayName, IReadOnlyCollection<string> roles)
    {
        var lifetime = audience == Audiences.Learn ? TimeSpan.FromDays(30) : TimeSpan.FromHours(12);
        var expires = DateTimeOffset.UtcNow.Add(lifetime);
        var claims = new Dictionary<string, object>
        {
            [KonnyakuClaims.Subject] = userId.ToString(),
            [KonnyakuClaims.Tenant] = tenantId.ToString(),
            [KonnyakuClaims.Name] = displayName,
            [KonnyakuClaims.Role] = roles.ToArray(),
        };
        var token = Handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = KonnyakuAuthExtensions.Issuer,
            Audience = audience,
            Claims = claims,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(KonnyakuAuthExtensions.SigningKey(configuration), SecurityAlgorithms.HmacSha256),
        });
        return new IssuedToken(token, audience, expires);
    }
}
