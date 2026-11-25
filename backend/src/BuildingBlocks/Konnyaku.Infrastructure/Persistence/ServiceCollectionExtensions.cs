using Konnyaku.Application.Tenancy;
using Konnyaku.Infrastructure.Auditing;
using Konnyaku.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Konnyaku.Infrastructure.Persistence;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers a module DbContext with its own connection string (<c>ConnectionStrings:{connectionName}</c>,
    /// a DB role limited to the module schema) and its migration history inside that schema.
    /// </summary>
    public static IServiceCollection AddModuleDbContext<TContext>(
        this IServiceCollection services, IConfiguration configuration, string connectionName, string schema)
        where TContext : ModuleDbContext
    {
        services.AddDbContext<TContext>((sp, options) =>
        {
            var connectionString = configuration.GetConnectionString(connectionName)
                ?? throw new InvalidOperationException($"Missing connection string '{connectionName}'.");
            options
                .UseNpgsql(connectionString, npgsql => npgsql
                    .MigrationsHistoryTable("__ef_migrations_history", schema)
                    .MigrationsAssembly(typeof(TContext).Assembly.FullName))
                .UseSnakeCaseNamingConvention()
                .AddInterceptors(
                    new TenantSessionInterceptor(sp.GetRequiredService<ITenantContext>()),
                    new AuditInterceptor(sp.GetService<ICurrentActor>() ?? SystemActor.Instance, sp.GetService<TimeProvider>() ?? TimeProvider.System));
        });
        return services;
    }
}
