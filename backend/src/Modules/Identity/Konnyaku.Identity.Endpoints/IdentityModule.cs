using System.Security.Claims;
using Konnyaku.Application.Authorization;
using Konnyaku.Identity.Application;
using Konnyaku.Identity.Contracts;
using Konnyaku.Identity.Domain;
using Konnyaku.Identity.Infrastructure;
using Konnyaku.Infrastructure.Auditing;
using Konnyaku.Infrastructure.Auth;
using Konnyaku.Infrastructure.Http;
using Konnyaku.Infrastructure.Messaging;
using Konnyaku.Infrastructure.Modules;
using Konnyaku.Infrastructure.Tenancy;
using Konnyaku.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Konnyaku.Identity.Endpoints;

public sealed record SignInResponse(string AccessToken, string Audience, DateTimeOffset ExpiresAt, string DisplayName, IReadOnlyList<string> Roles, bool MustChangePassword);

public sealed class IdentityModule : IModule
{
    public string Name => "identity";

    public IReadOnlyList<Type> DbContextTypes => [typeof(IdentityDbContext)];

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddIdentityInfrastructure(configuration, Name);
        services.AddScoped<StaffLoginHandler>();
        services.AddScoped<LearnerLoginHandler>();
        services.AddScoped<ChangeLearnerPasswordHandler>();
        services.AddScoped<CreateLearnerAccountHandler>();
        services.AddScoped<BootstrapTenantAdminHandler>();
        services.AddScoped<AddStaffMemberHandler>();
        services.AddScoped<StaffQueries>();
        services.AddScoped<StaffList>();
        services.AddScoped<Konnyaku.Application.Messaging.IIntegrationEventHandler<Konnyaku.Crm.Contracts.StudentAnonymizedV1>, StudentAnonymizedConsumer>();
        services.AddScoped<StaffAdminHandler>();
    }

    public void AddWorkerServices(IServiceCollection services) => services.AddOutboxProcessor<IdentityDbContext>();

    public void MapEndpoints(RouteGroupBuilder group)
    {
        group.MapPost("/staff/login", async (StaffLoginCommand command, StaffLoginHandler handler, ITokenIssuer tokens, CancellationToken ct) =>
            ApiResults.From(await handler.HandleAsync(command, ct), s => Results.Ok(Issue(tokens, s, Audiences.Staff))));

        group.MapPost("/learn/login", async (LearnerLoginCommand command, LearnerLoginHandler handler, ITokenIssuer tokens, CancellationToken ct) =>
            ApiResults.From(await handler.HandleAsync(command, ct), s => Results.Ok(Issue(tokens, s, Audiences.Learn))));

        group.MapPost("/learn/change-password", async (ChangePasswordCommand command, ClaimsPrincipal user, ChangeLearnerPasswordHandler handler, CancellationToken ct) =>
                ApiResults.From(await handler.HandleAsync(user.UserId()!.Value, command, ct), _ => Results.NoContent()))
            .RequireAuthorization(Policies.Learn);

        group.MapGet("/me", async (ClaimsPrincipal user, IIdentityApi identity, CancellationToken ct) =>
                await identity.GetMemberAsync(user.UserId()!.Value, ct) is { } me ? Results.Ok(me) : Results.Unauthorized())
            .RequireAuthorization();

        group.MapPost("/learners", async (CreateLearnerAccountCommand command, CreateLearnerAccountHandler handler, CancellationToken ct) =>
                ApiResults.From(await handler.HandleAsync(command, ct), a => Results.Created($"learners/{a.UserId}", a)))
            .RequireAuthorization(p => p.RequireAuthenticatedUser()
                .RequireClaim(KonnyakuClaims.Audience, Audiences.Staff)
                .RequireRole(Roles.TenantAdmin, Roles.Consultant));

        // What the signed-in member may change (the ops screens hide the rest; the API checks again on every write).
        group.MapGet("/me/access", async (IAccessControl access, CancellationToken ct) => Results.Ok(await access.ListAsync(ct)))
            .RequireAuthorization(Policies.Staff);

        group.MapPost("/staff", async (AddStaffMemberCommand command, AddStaffMemberHandler handler, CancellationToken ct) =>
                ApiResults.From(await handler.HandleAsync(command, ct), id => Results.Ok(new { userId = id })))
            .RequirePermission(Permissions.Staff);

        // Colleagues (names, emails, roles, branches) for members who change something: coordinators pick teachers,
        // accountants set rates, consultants hand over leads. A teacher-only member does not list staff.
        group.MapGet("/staff", async ([AsParameters] StaffFilter filter, [AsParameters] ListParams list, StaffList staff, CancellationToken ct) =>
                Results.Ok(await staff.ListAsync(filter, list.ToRequest(StaffList.Sorts), ct)))
            .RequirePermission([.. Permissions.All]);

        group.MapPut("/staff/{userId:guid}", async (Guid userId, UpdateStaffMemberCommand command, StaffAdminHandler handler, CancellationToken ct) =>
                ApiResults.From(await handler.UpdateAsync(userId, command, ct), Results.Ok))
            .RequirePermission(Permissions.Staff);

        group.MapPost("/staff/{userId:guid}/deactivate", async (Guid userId, ClaimsPrincipal user, StaffAdminHandler handler, CancellationToken ct) =>
                ApiResults.From(await handler.SuspendAsync(userId, user.UserId()!.Value, ct), Results.Ok))
            .RequirePermission(Permissions.Staff);

        group.MapPost("/staff/{userId:guid}/activate", async (Guid userId, StaffAdminHandler handler, CancellationToken ct) =>
                ApiResults.From(await handler.ActivateAsync(userId, ct), Results.Ok))
            .RequirePermission(Permissions.Staff);

        group.MapChangeHistory<IdentityDbContext>();

        // Platform operation: runs in its own scope with the target tenant as context.
        group.MapPost("/bootstrap/tenant-admin", async (BootstrapTenantAdminCommand command, IServiceScopeFactory scopes, CancellationToken ct) =>
            {
                await using var scope = scopes.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<TenantContext>().Set(command.TenantId);
                var result = await scope.ServiceProvider.GetRequiredService<BootstrapTenantAdminHandler>().HandleAsync(command, ct);
                return ApiResults.From(result, id => Results.Ok(new { userId = id }));
            })
            .RequirePlatformKey();
    }

    private static SignInResponse Issue(ITokenIssuer tokens, SignedIn signedIn, string audience)
    {
        var token = tokens.Issue(signedIn.UserId, signedIn.TenantId, audience, signedIn.DisplayName, signedIn.Roles);
        return new SignInResponse(token.AccessToken, audience, token.ExpiresAt, signedIn.DisplayName, signedIn.Roles, signedIn.MustChangePassword);
    }
}
