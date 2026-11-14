using System.Security.Claims;
using Konnyaku.Application.Messaging;
using Konnyaku.Infrastructure.Auth;
using Konnyaku.Infrastructure.Http;
using Konnyaku.Infrastructure.Messaging;
using Konnyaku.Infrastructure.Modules;
using Konnyaku.Scheduling.Contracts;
using Konnyaku.Workspace.Application;
using Konnyaku.Workspace.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Konnyaku.Workspace.Endpoints;

public sealed class WorkspaceModule : IModule
{
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
        services.AddScoped<IIntegrationEventHandler<SessionScheduledV1>, SessionScheduledConsumer>();
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

        group.MapPost("/dev/sessions/{id:guid}/simulate", async (Guid id, SimulateMeetingCommand command, SimulateMeetingHandler handler, CancellationToken ct) =>
                ApiResults.From(await handler.HandleAsync(id, command, ct), _ => Results.Accepted()))
            .RequireAuthorization(p => p.RequireAuthenticatedUser()
                .RequireClaim(KonnyakuClaims.Audience, Audiences.Staff)
                .RequireRole("TenantAdmin", "Coordinator"));
    }
}
