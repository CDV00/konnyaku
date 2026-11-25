using Konnyaku.Application.Tenancy;
using Konnyaku.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

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
        var connectionString = new Lazy<string>(() => WithPoolSettings(
            configuration.GetConnectionString(connectionName)
                ?? throw new InvalidOperationException($"Missing connection string '{connectionName}'."),
            configuration));
        services.AddDbContext<TContext>((sp, options) =>
        {
            options
                .UseNpgsql(connectionString.Value, npgsql => npgsql
                    .MigrationsHistoryTable("__ef_migrations_history", schema)
                    .MigrationsAssembly(typeof(TContext).Assembly.FullName))
                .UseSnakeCaseNamingConvention()
                .AddInterceptors(new TenantSessionInterceptor(sp.GetRequiredService<ITenantContext>()));
        });
        return services;
    }

    /// <summary>
    /// Every module has its own role, hence its own connection pool: a process holds up to 11 pools. Deployments cap
    /// them with <c>Database:MaxPoolSize</c> and <c>Database:ConnectionIdleLifetime</c> (seconds) so that the sum over
    /// all API/Worker replicas stays under PostgreSQL <c>max_connections</c> and idle connections (a few MB of
    /// PostgreSQL memory each) are closed sooner. Unset keys keep the connection string (Npgsql defaults: 100, 300 s).
    /// </summary>
    public static string WithPoolSettings(string connectionString, IConfiguration configuration)
    {
        var maxPoolSize = configuration.GetValue<int?>("Database:MaxPoolSize");
        var idleLifetime = configuration.GetValue<int?>("Database:ConnectionIdleLifetime");
        if (maxPoolSize is null && idleLifetime is null)
        {
            return connectionString;
        }

        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (maxPoolSize is { } max)
        {
            builder.MaxPoolSize = max;
        }

        if (idleLifetime is { } idle)
        {
            builder.ConnectionIdleLifetime = idle;
        }

        return builder.ConnectionString;
    }
}
