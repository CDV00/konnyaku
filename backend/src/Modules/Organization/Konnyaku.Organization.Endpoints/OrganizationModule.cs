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
    }
}
