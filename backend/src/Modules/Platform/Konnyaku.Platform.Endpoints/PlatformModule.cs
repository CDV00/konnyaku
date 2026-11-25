using System.Security.Claims;
using Konnyaku.Application.Tenancy;
using Konnyaku.Infrastructure.Auth;
using Konnyaku.Infrastructure.Http;
using Konnyaku.Infrastructure.Messaging;
using Konnyaku.Infrastructure.Modules;
using Konnyaku.Platform.Application;
using Konnyaku.Platform.Contracts;
using Konnyaku.Platform.Infrastructure;
using Konnyaku.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Konnyaku.Platform.Endpoints;

public sealed record SignInStep(string Challenge, DateTimeOffset ExpiresAt, bool EnrollmentRequired, string? Secret, string? OtpAuthUri);

public sealed record VerifyRequest(string? Challenge, string? Code, string? RecoveryCode);

public sealed record OperatorSession(
    string AccessToken, DateTimeOffset ExpiresAt, string DisplayName, bool MustChangePassword, IReadOnlyList<string>? RecoveryCodes, int RecoveryCodesLeft);

public sealed class PlatformModule : IModule
{
    public const string TenantHostHeader = "X-Tenant-Host";
    private const string ActorItem = "platform.actor";

    public string Name => "platform";

    public IReadOnlyList<Type> DbContextTypes => [typeof(PlatformDbContext)];

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddPlatformInfrastructure(configuration, Name);
        services.AddPlatformAuth();
        services.AddScoped<AuditLog>();
        services.AddScoped<AuditQueries>();
        services.AddScoped<CreateTenantHandler>();
        services.AddScoped<OperatorHandlers>();
        services.AddScoped<TenantConsole>();
    }

    public void AddWorkerServices(IServiceCollection services) => services.AddOutboxProcessor<PlatformDbContext>();

    public void MapEndpoints(RouteGroupBuilder group)
    {
        // Public: branding, enabled locales and status of the tenant owning the current host.
        group.MapGet("/tenant", async (ITenantContext tenant, IPlatformApi platform, CancellationToken ct) =>
            tenant.TenantId is { } id && await platform.GetTenantAsync(id, ct) is { } info
                ? Results.Ok(info)
                : ApiResults.Error(Error.NotFound("platform.tenant_unknown")));

        MapAutomation(group);
        MapSignIn(group.MapGroup("/auth").AddEndpointFilter(AdminHostOnly));
        MapConsole(group.MapGroup("/console")
            .AddEndpointFilter(AdminHostOnly)
            .RequireAuthorization(PlatformAuth.Policy)
            .AddEndpointFilter(SignedInOperator));
    }

    /// <summary>Scripts and seeds holding the shared <c>X-Platform-Key</c> (no person attached; audited as "automation").</summary>
    private static void MapAutomation(RouteGroupBuilder group)
    {
        group.MapPost("/tenants", async (CreateTenantCommand command, CreateTenantHandler handler, HttpContext http, CancellationToken ct) =>
                ApiResults.From(await handler.HandleAsync(command, PlatformActor.Automation(Ip(http)), ct), t => Results.Created($"tenants/{t.Id}", t)))
            .RequirePlatformKey();

        // First operator of an installation; refused once any operator exists. Later operators are invited from the console.
        group.MapPost("/operators/bootstrap", async (BootstrapOperatorCommand command, OperatorHandlers handler, HttpContext http, CancellationToken ct) =>
                ApiResults.From(await handler.BootstrapAsync(command, PlatformActor.Automation(Ip(http)), ct), Results.Ok))
            .RequirePlatformKey();
    }

    private static void MapSignIn(RouteGroupBuilder auth)
    {
        auth.MapPost("/login", async (OperatorSignInCommand command, OperatorHandlers handler, IPlatformTokenService tokens, HttpContext http, CancellationToken ct) =>
                ApiResults.From(await handler.SignInAsync(command, Ip(http), ct), accepted =>
                {
                    var (challenge, expires) = tokens.IssueChallenge(accepted.OperatorId);
                    return Results.Ok(new SignInStep(challenge, expires, accepted.EnrollmentRequired, accepted.Secret, accepted.OtpAuthUri));
                }))
            .RequireRateLimiting(RateLimitPolicies.PublicForms);

        auth.MapPost("/verify", async (VerifyRequest request, OperatorHandlers handler, IPlatformTokenService tokens, HttpContext http, CancellationToken ct) =>
            {
                if (await tokens.ValidateChallengeAsync(request.Challenge) is not { } operatorId)
                {
                    return ApiResults.Error(Error.Validation("platform.challenge_expired"));
                }

                var result = await handler.VerifySecondFactorAsync(operatorId, new SecondFactorCommand(request.Code, request.RecoveryCode), Ip(http), ct);
                return ApiResults.From(result, signedIn =>
                {
                    var token = tokens.IssueAccess(signedIn.OperatorId, signedIn.DisplayName);
                    return Results.Ok(new OperatorSession(
                        token.AccessToken, token.ExpiresAt, signedIn.DisplayName, signedIn.MustChangePassword, signedIn.RecoveryCodes, signedIn.RecoveryCodesLeft));
                });
            })
            .RequireRateLimiting(RateLimitPolicies.PublicForms);
    }

    private static void MapConsole(RouteGroupBuilder console)
    {
        console.MapGet("/me", async (ClaimsPrincipal user, OperatorHandlers handler, CancellationToken ct) =>
            await handler.GetAsync(user.UserId()!.Value, ct) is { } me ? Results.Ok(me) : Results.Unauthorized());

        console.MapPost("/me/password", async (ChangeOperatorPasswordCommand command, ClaimsPrincipal user, OperatorHandlers handler, HttpContext http, CancellationToken ct) =>
            ApiResults.From(await handler.ChangePasswordAsync(user.UserId()!.Value, command, Actor(http), ct), _ => Results.Ok(new { })));

        console.MapGet("/tenants", async (TenantConsole tenants, CancellationToken ct) => Results.Ok(await tenants.ListAsync(ct)));

        console.MapPost("/tenants", async (ProvisionTenantCommand command, TenantConsole tenants, HttpContext http, CancellationToken ct) =>
            ApiResults.From(await tenants.ProvisionAsync(command, Actor(http), ct), t => Results.Created($"console/tenants/{t.TenantId}", t)));

        console.MapGet("/tenants/{id:guid}", async (Guid id, TenantConsole tenants, CancellationToken ct) =>
            await tenants.GetAsync(id, ct) is { } tenant ? Results.Ok(tenant) : ApiResults.Error(Error.NotFound("platform.tenant_not_found")));

        console.MapPost("/tenants/{id:guid}/admins", async (Guid id, TenantAdminCommand command, TenantConsole tenants, HttpContext http, CancellationToken ct) =>
            ApiResults.From(await tenants.AddAdminAsync(id, command, Actor(http), ct), Results.Ok));

        console.MapPost("/tenants/{id:guid}/suspend", async (Guid id, SuspendTenantCommand command, TenantConsole tenants, HttpContext http, CancellationToken ct) =>
            ApiResults.From(await tenants.SuspendAsync(id, command, Actor(http), ct), Results.Ok));

        console.MapPost("/tenants/{id:guid}/resume", async (Guid id, TenantConsole tenants, HttpContext http, CancellationToken ct) =>
            ApiResults.From(await tenants.ResumeAsync(id, Actor(http), ct), Results.Ok));

        console.MapPost("/tenants/{id:guid}/domains", async (Guid id, TenantDomainCommand command, TenantConsole tenants, HttpContext http, CancellationToken ct) =>
            ApiResults.From(await tenants.AddDomainAsync(id, command, Actor(http), ct), Results.Ok));

        console.MapPost("/tenants/{id:guid}/domains/remove", async (Guid id, TenantDomainCommand command, TenantConsole tenants, HttpContext http, CancellationToken ct) =>
            ApiResults.From(await tenants.RemoveDomainAsync(id, command, Actor(http), ct), Results.Ok));

        console.MapGet("/audit", async (Guid? tenantId, DateTimeOffset? before, int? take, AuditQueries audit, CancellationToken ct) =>
            Results.Ok(await audit.ListAsync(tenantId, before, take ?? 100, ct)));

        console.MapGet("/operators", async (OperatorHandlers handler, CancellationToken ct) => Results.Ok(await handler.ListAsync(ct)));

        console.MapPost("/operators", async (InviteOperatorCommand command, OperatorHandlers handler, HttpContext http, CancellationToken ct) =>
            ApiResults.From(await handler.InviteAsync(command, Actor(http), ct), Results.Ok));

        console.MapPost("/operators/{id:guid}/disable", async (Guid id, OperatorHandlers handler, HttpContext http, CancellationToken ct) =>
            ApiResults.From(await handler.SetStatusAsync(id, active: false, Actor(http), ct), Results.Ok));

        console.MapPost("/operators/{id:guid}/enable", async (Guid id, OperatorHandlers handler, HttpContext http, CancellationToken ct) =>
            ApiResults.From(await handler.SetStatusAsync(id, active: true, Actor(http), ct), Results.Ok));
    }

    /// <summary>
    /// The console and its sign-in answer only on the admin host (<c>Platform:AdminHost</c>), never on a tenant host:
    /// 404 elsewhere, as if the routes did not exist. The web app enforces the same rule in proxy.ts.
    /// </summary>
    private static async ValueTask<object?> AdminHostOnly(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var host = http.Request.Headers.TryGetValue(TenantHostHeader, out var forwarded) && !string.IsNullOrWhiteSpace(forwarded)
            ? forwarded.ToString()
            : http.Request.Host.Host;
        return http.RequestServices.GetRequiredService<PlatformOptions>().IsAdminHost(host) ? await next(context) : Results.NotFound();
    }

    /// <summary>
    /// The token's operator must still exist and be active (a disabled operator loses access at once, not at token expiry).
    /// With a temporary password, only reading the profile and changing the password are allowed.
    /// </summary>
    private static async ValueTask<object?> SignedInOperator(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (http.User.UserId() is not { } operatorId
            || await http.RequestServices.GetRequiredService<OperatorHandlers>().GetAsync(operatorId, http.RequestAborted) is not { Status: "Active" } account)
        {
            return Results.Unauthorized();
        }

        var path = http.Request.Path.Value ?? string.Empty;
        if (account.MustChangePassword && !path.EndsWith("/console/me", StringComparison.Ordinal) && !path.EndsWith("/console/me/password", StringComparison.Ordinal))
        {
            return Results.Json(new ErrorBody("platform.password_change_required", new Dictionary<string, object?>()), statusCode: StatusCodes.Status403Forbidden);
        }

        http.Items[ActorItem] = new PlatformActor(account.Id, account.Email, Ip(http));
        return await next(context);
    }

    private static PlatformActor Actor(HttpContext http) => (PlatformActor)http.Items[ActorItem]!;

    private static string? Ip(HttpContext http) => http.Connection.RemoteIpAddress?.ToString();
}
