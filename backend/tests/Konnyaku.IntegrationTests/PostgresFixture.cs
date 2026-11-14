using System.Runtime.CompilerServices;
using Konnyaku.Infrastructure.Modules;
using Konnyaku.Organization.Endpoints;
using Konnyaku.Organization.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Konnyaku.IntegrationTests;

/// <summary>
/// Real PostgreSQL with the same bootstrap script and migrations as docker compose, so RLS, schema roles and
/// exclusion constraints are tested for real (they cannot be faked in-memory).
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17")
        .WithUsername("konnyaku")
        .WithPassword("konnyaku_dev")
        .WithDatabase("konnyaku")
        .Build();

    public string OwnerConnectionString => _container.GetConnectionString();

    public string ModuleConnectionString(string module) => new NpgsqlConnectionStringBuilder(OwnerConnectionString)
    {
        Username = $"kk_{module}",
        Password = $"kk_{module}_dev",
    }.ConnectionString;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        var script = await File.ReadAllTextAsync(RepoFile("infra/docker/postgres/init/01-modules.sql"));
        var exec = await _container.ExecScriptAsync(script);
        if (exec.ExitCode != 0)
        {
            throw new InvalidOperationException(exec.Stderr);
        }

        using var host = BuildHost(OwnerConnectionString);
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<OrganizationDbContext>().Database.MigrateAsync();
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    /// <summary>Host wired exactly like Konnyaku.Api, but connected as the module role.</summary>
    public IHost BuildHost(string? connectionString = null, Action<IServiceCollection>? configure = null)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:organization"] = connectionString ?? ModuleConnectionString("organization"),
        });
        builder.Services.AddLogging();
        builder.AddModules(HostRole.Api, new OrganizationModule());
        configure?.Invoke(builder.Services);
        return builder.Build();
    }

    private static string RepoFile(string relative, [CallerFilePath] string sourceFile = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(sourceFile)!);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "infra")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir?.FullName ?? throw new DirectoryNotFoundException("Repository root not found."), relative);
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
