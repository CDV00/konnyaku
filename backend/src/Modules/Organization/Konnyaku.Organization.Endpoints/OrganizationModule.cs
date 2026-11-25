using Konnyaku.Application.Authorization;
using Konnyaku.Infrastructure.Auditing;
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
        services.AddScoped<BranchAdminHandler>();
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
            .RequirePermission(Permissions.Branches);

        group.MapPut("/branches/{id:guid}", async (Guid id, UpdateBranchCommand command, BranchAdminHandler handler, CancellationToken ct) =>
                ApiResults.From(await handler.UpdateAsync(id, command, ct), Results.Ok))
            .RequirePermission(Permissions.Branches);
        group.MapPost("/branches/{id:guid}/deactivate", async (Guid id, BranchAdminHandler handler, CancellationToken ct) =>
                ApiResults.From(await handler.SetActiveAsync(id, false, ct), Results.Ok))
            .RequirePermission(Permissions.Branches);
        group.MapPost("/branches/{id:guid}/activate", async (Guid id, BranchAdminHandler handler, CancellationToken ct) =>
                ApiResults.From(await handler.SetActiveAsync(id, true, ct), Results.Ok))
            .RequirePermission(Permissions.Branches);

        group.MapGet("/holidays", async (DateOnly from, DateOnly to, CalendarHandlers handlers, CancellationToken ct) =>
                Results.Ok(await handlers.ListHolidaysAsync(from, to, ct)))
            .RequireAuthorization(Policies.Staff);

        group.MapPost("/holidays", async (AddHolidayCommand command, CalendarHandlers handlers, CancellationToken ct) =>
                ApiResults.From(await handlers.AddHolidayAsync(command, ct), h => Results.Created($"holidays/{h.Id}", h)))
            .RequirePermission(Permissions.Holidays);
        group.MapPut("/holidays/{id:guid}", async (Guid id, AddHolidayCommand command, CalendarHandlers handlers, CancellationToken ct) =>
                ApiResults.From(await handlers.UpdateHolidayAsync(id, command, ct), Results.Ok))
            .RequirePermission(Permissions.Holidays);
        group.MapDelete("/holidays/{id:guid}", async (Guid id, CalendarHandlers handlers, CancellationToken ct) =>
                ApiResults.From(await handlers.DeleteHolidayAsync(id, ct), _ => Results.NoContent()))
            .RequirePermission(Permissions.Holidays);

        group.MapGet("/rooms", async (Guid? branchId, CalendarHandlers handlers, CancellationToken ct) =>
                Results.Ok(await handlers.ListRoomsAsync(branchId, ct)))
            .RequireAuthorization(Policies.Staff);
        group.MapPost("/branches/{branchId:guid}/rooms", async (Guid branchId, AddRoomCommand command, CalendarHandlers handlers, CancellationToken ct) =>
                ApiResults.From(await handlers.AddRoomAsync(branchId, command, ct), x => Results.Created($"rooms/{x.Id}", x)))
            .RequirePermission(Permissions.Rooms);
        group.MapPut("/rooms/{id:guid}", async (Guid id, UpdateRoomCommand command, CalendarHandlers handlers, CancellationToken ct) =>
                ApiResults.From(await handlers.UpdateRoomAsync(id, command, ct), Results.Ok))
            .RequirePermission(Permissions.Rooms);
        group.MapPost("/rooms/{id:guid}/deactivate", async (Guid id, CalendarHandlers handlers, CancellationToken ct) =>
                ApiResults.From(await handlers.SetRoomActiveAsync(id, false, ct), Results.Ok))
            .RequirePermission(Permissions.Rooms);
        group.MapPost("/rooms/{id:guid}/activate", async (Guid id, CalendarHandlers handlers, CancellationToken ct) =>
                ApiResults.From(await handlers.SetRoomActiveAsync(id, true, ct), Results.Ok))
            .RequirePermission(Permissions.Rooms);

        group.MapChangeHistory<OrganizationDbContext>(Permissions.Branches, Permissions.Rooms, Permissions.Holidays);
    }
}
