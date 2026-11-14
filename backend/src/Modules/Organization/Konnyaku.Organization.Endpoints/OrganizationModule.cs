using Konnyaku.Infrastructure.Auth;
using Konnyaku.Infrastructure.Http;
using Konnyaku.Infrastructure.Messaging;
using Konnyaku.Infrastructure.Modules;
using Konnyaku.Organization.Application;
using Konnyaku.Organization.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Konnyaku.Organization.Endpoints;

public sealed class OrganizationModule : IModule
{
    public string Name => "organization";

    public IReadOnlyList<Type> DbContextTypes => [typeof(OrganizationDbContext)];

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOrganizationInfrastructure(configuration, Name);
        services.AddScoped<CreateBranchHandler>();
        services.AddScoped<ListBranchesHandler>();
        services.AddScoped<CalendarHandlers>();
    }

    public void AddWorkerServices(IServiceCollection services) =>
        services.AddOutboxProcessor<OrganizationDbContext>();

    public void MapEndpoints(RouteGroupBuilder group)
    {
        group.MapGet("/branches", async (ListBranchesHandler handler, CancellationToken ct) =>
            Results.Ok(await handler.HandleAsync(ct)))
            .RequireAuthorization(Policies.Staff);

        group.MapPost("/branches", async (CreateBranchCommand command, CreateBranchHandler handler, CancellationToken ct) =>
            ApiResults.From(await handler.HandleAsync(command, ct), branch => Results.Created($"branches/{branch.Id}", branch)))
            .RequireAuthorization(Policies.TenantAdmin);

        group.MapGet("/holidays", async (DateOnly from, DateOnly to, CalendarHandlers handlers, CancellationToken ct) =>
                Results.Ok(await handlers.ListHolidaysAsync(from, to, ct)))
            .RequireAuthorization(Policies.Staff);

        group.MapPost("/holidays", async (AddHolidayCommand command, CalendarHandlers handlers, CancellationToken ct) =>
                ApiResults.From(await handlers.AddHolidayAsync(command, ct), h => Results.Created($"holidays/{h.Id}", h)))
            .RequireAuthorization(Policies.TenantAdmin);

        group.MapPost("/branches/{branchId:guid}/rooms", async (Guid branchId, AddRoomCommand command, CalendarHandlers handlers, CancellationToken ct) =>
                ApiResults.From(await handlers.AddRoomAsync(branchId, command, ct), x => Results.Created($"rooms/{x.Id}", x)))
            .RequireAuthorization(Policies.TenantAdmin);
    }
}
