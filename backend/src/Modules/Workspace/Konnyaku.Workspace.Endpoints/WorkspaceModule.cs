using System.Security.Claims;
using Konnyaku.Application.Authorization;
using Konnyaku.Application.Messaging;
using Konnyaku.Infrastructure.Auth;
using Konnyaku.Infrastructure.Http;
using Konnyaku.Infrastructure.Messaging;
using Konnyaku.Infrastructure.Modules;
using Konnyaku.Infrastructure.Tenancy;
using Konnyaku.Scheduling.Contracts;
using Konnyaku.Workspace.Application;
using Konnyaku.Workspace.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Konnyaku.Workspace.Endpoints;

public sealed class WorkspaceModule : IModule
{
    public const string RoomServiceTokenHeader = "X-Rtc-Token";

    public string Name => "workspace";

    public IReadOnlyList<Type> DbContextTypes => [typeof(WorkspaceDbContext)];

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddWorkspaceInfrastructure(configuration, Name);
        services.AddScoped<HostAllocator>();
        services.AddScoped<ProvisionMeetingHandler>();
        services.AddScoped<JoinHandler>();
        services.AddScoped<PoolHandlers>();
        services.AddScoped<SimulateMeetingHandler>();
        services.AddScoped<RoomTicketHandler>();
        services.AddScoped<RoomEventsHandler>();
        services.AddScoped<IIntegrationEventHandler<SessionScheduledV1>, SessionScheduledConsumer>();
        services.AddScoped<IIntegrationEventHandler<Konnyaku.Crm.Contracts.StudentAnonymizedV1>, StudentAnonymizedConsumer>();
        services.AddScoped<IIntegrationEventHandler<SessionRescheduledV1>, SessionRescheduledConsumer>();
        services.AddScoped<IIntegrationEventHandler<SessionCancelledV1>, SessionCancelledConsumer>();
        services.AddScoped<IIntegrationEventHandler<Konnyaku.Learning.Contracts.RecordingAccessGrantedV1>, RecordingAccessGrantedConsumer>();
    }

    public void AddWorkerServices(IServiceCollection services) => services.AddOutboxProcessor<WorkspaceDbContext>();

    public void MapEndpoints(RouteGroupBuilder group)
    {
        var admin = group.MapGroup("").RequireAuthorization(Policies.TenantAdmin);
        admin.MapPut("/connection", async (SetConnectionCommand command, PoolHandlers handlers, CancellationToken ct) =>
            ApiResults.From(await handlers.SetConnectionAsync(command, ct), p => Results.Ok(new { provider = p })));
        admin.MapPost("/hosts", async (AddHostCommand command, PoolHandlers handlers, CancellationToken ct) =>
            ApiResults.From(await handlers.AddHostAsync(command, ct), h => Results.Created($"hosts/{h.Id}", h)));
        admin.MapGet("/hosts", async (PoolHandlers handlers, CancellationToken ct) => Results.Ok(await handlers.ListHostsAsync(ct)));
        admin.MapGet("/leases", async (DateTimeOffset from, DateTimeOffset to, PoolHandlers handlers, CancellationToken ct) =>
            Results.Ok(await handlers.ListLeasesAsync(from, to, ct)));

        // Join link for teachers (staff realm) and learners (learn realm): permission check, then the meeting URI.
        group.MapGet("/sessions/{id:guid}/join", async (Guid id, ClaimsPrincipal user, JoinHandler handler, CancellationToken ct) =>
            {
                var isLearner = user.HasClaim(KonnyakuClaims.Audience, Audiences.Learn);
                var roles = user.FindAll(KonnyakuClaims.Role).Select(c => c.Value).ToList();
                return ApiResults.From(await handler.HandleAsync(id, user.UserId()!.Value, isLearner, roles, ct), Results.Ok);
            })
            .RequireAuthorization();

        // Built-in classroom: a fresh short-lived ticket for the signaling service, on every (re)connect of the room page.
        group.MapPost("/rooms/{code}/ticket", async (string code, ClaimsPrincipal user, RoomTicketHandler handler, CancellationToken ct) =>
            {
                var isLearner = user.HasClaim(KonnyakuClaims.Audience, Audiences.Learn);
                var roles = user.FindAll(KonnyakuClaims.Role).Select(c => c.Value).ToList();
                var name = user.FindFirstValue(KonnyakuClaims.Name) ?? string.Empty;
                return ApiResults.From(await handler.HandleAsync(code, user.UserId()!.Value, name, isLearner, roles, ct), Results.Ok);
            })
            .RequireAuthorization();

        // Reports of the signaling service (joined / left / ended). Not reachable from the internet (the edge only
        // routes to the web app); signed with the secret shared with the signaling service. The tenant comes from the
        // report, which carries the tenant of the ticket the signaling service verified.
        group.MapPost("/internal/rooms/events", async (RoomEventCommand command, HttpContext http, TenantContext tenant,
                IRoomTickets tickets, RoomEventsHandler handler, CancellationToken ct) =>
            {
                if (!await tickets.IsValidServiceTokenAsync(http.Request.Headers[RoomServiceTokenHeader].ToString()))
                {
                    return Results.Unauthorized();
                }

                if (tenant.TenantId is { } current && current != command.TenantId)
                {
                    return Results.Forbid();
                }

                tenant.Set(command.TenantId);
                return ApiResults.From(await handler.HandleAsync(command, ct), _ => Results.NoContent());
            })
            .AllowAnonymous();

        // Development tool: fakes a meeting that took place (participants, recording), which then drives attendance and
        // timesheets. Off unless Workspace:DevTools is true (default: on in Development only), whatever the provider.
        group.MapPost("/dev/sessions/{id:guid}/simulate", async (Guid id, SimulateMeetingCommand command, SimulateMeetingHandler handler, CancellationToken ct) =>
                ApiResults.From(await handler.HandleAsync(id, command, ct), _ => Results.Accepted()))
            .AddEndpointFilter(async (context, next) => DevToolsEnabled(context.HttpContext.RequestServices) ? await next(context) : Results.NotFound())
            .RequirePermission(Permissions.Schedules);
    }

    public static bool DevToolsEnabled(IServiceProvider services) =>
        services.GetRequiredService<IConfiguration>().GetValue<bool?>("Workspace:DevTools")
        ?? services.GetRequiredService<IHostEnvironment>().IsDevelopment();
}
