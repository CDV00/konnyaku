using Konnyaku.Application.Persistence;
using Konnyaku.Application.Tenancy;
using Konnyaku.Infrastructure.Auditing;
using Konnyaku.Infrastructure.Persistence;
using Konnyaku.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Konnyaku.Infrastructure.Modules;

/// <summary>
/// A bounded context that registers itself. A host is only a list of modules, so moving a module into its own
/// service means building a host with a different list.
/// </summary>
public interface IModule
{
    /// <summary>Route prefix (<c>/api/{Name}</c>), connection string name and configuration section.</summary>
    string Name { get; }

    IReadOnlyList<Type> DbContextTypes => [];

    void AddServices(IServiceCollection services, IConfiguration configuration);

    void MapEndpoints(RouteGroupBuilder group)
    {
    }

    /// <summary>Background work (outbox dispatch, jobs) that runs only in worker hosts.</summary>
    void AddWorkerServices(IServiceCollection services)
    {
    }
}

public enum HostRole
{
    Api,
    Worker,
    Migrator,
}

public sealed class ModuleCatalog(IReadOnlyList<IModule> modules)
{
    public IReadOnlyList<IModule> Modules { get; } = modules;
}

public static class ModuleHostExtensions
{
    public static IHostApplicationBuilder AddModules(this IHostApplicationBuilder builder, HostRole role, params IModule[] modules)
    {
        builder.Services.AddScoped<TenantContext>();
        builder.Services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        builder.Services.AddSingleton(new ModuleCatalog(modules));
        builder.Services.AddSingleton<IDbErrorClassifier, NpgsqlErrorClassifier>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<ICurrentActor, HttpCurrentActor>();

        foreach (var module in modules)
        {
            module.AddServices(builder.Services, builder.Configuration);
            if (role == HostRole.Worker)
            {
                module.AddWorkerServices(builder.Services);
            }
        }

        return builder;
    }

    public static WebApplication MapModules(this WebApplication app)
    {
        foreach (var module in app.Services.GetRequiredService<ModuleCatalog>().Modules)
        {
            module.MapEndpoints(app.MapGroup($"/api/{module.Name}").WithTags(module.Name));
        }

        return app;
    }
}
