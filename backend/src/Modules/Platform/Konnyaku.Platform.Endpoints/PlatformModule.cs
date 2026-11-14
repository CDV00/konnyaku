using Konnyaku.Application.Tenancy;
using Konnyaku.Infrastructure.Auth;
using Konnyaku.Infrastructure.Http;
using Konnyaku.Infrastructure.Messaging;
using Konnyaku.Infrastructure.Modules;
using Konnyaku.Platform.Application;
using Konnyaku.Platform.Contracts;
using Konnyaku.Platform.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Konnyaku.Platform.Endpoints;

public sealed class PlatformModule : IModule
{
    public string Name => "platform";

    public IReadOnlyList<Type> DbContextTypes => [typeof(PlatformDbContext)];

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddPlatformInfrastructure(configuration, Name);
        services.AddScoped<CreateTenantHandler>();
    }

    public void AddWorkerServices(IServiceCollection services) => services.AddOutboxProcessor<PlatformDbContext>();

    public void MapEndpoints(RouteGroupBuilder group)
    {
        // Public: branding and enabled locales of the tenant owning the current host.
        group.MapGet("/tenant", async (ITenantContext tenant, IPlatformApi platform, CancellationToken ct) =>
            tenant.TenantId is { } id && await platform.GetTenantAsync(id, ct) is { } info
                ? Results.Ok(info)
                : ApiResults.Error(Konnyaku.SharedKernel.Error.NotFound("platform.tenant_unknown")));

        group.MapPost("/tenants", async (CreateTenantCommand command, CreateTenantHandler handler, CancellationToken ct) =>
                ApiResults.From(await handler.HandleAsync(command, ct), t => Results.Created($"tenants/{t.Id}", t)))
            .RequirePlatformKey();
    }
}
