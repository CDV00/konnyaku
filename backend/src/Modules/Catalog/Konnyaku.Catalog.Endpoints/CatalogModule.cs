using Konnyaku.Catalog.Contracts;
using Konnyaku.Catalog.Infrastructure;
using Konnyaku.Infrastructure.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Konnyaku.Catalog.Endpoints;

public sealed class CatalogModule : IModule
{
    public string Name => "catalog";

    public IReadOnlyList<Type> DbContextTypes => [typeof(CatalogDbContext)];

    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddCatalogInfrastructure(configuration, Name);

    public void MapEndpoints(RouteGroupBuilder group)
    {
        group.MapGet("/languages", async (ICatalogApi catalog, CancellationToken ct) =>
            Results.Ok(await catalog.ListLanguagesAsync(ct)));

        group.MapGet("/frameworks", async (string? language, ICatalogApi catalog, CancellationToken ct) =>
            Results.Ok(await catalog.ListFrameworksAsync(language, ct)));
    }
}
