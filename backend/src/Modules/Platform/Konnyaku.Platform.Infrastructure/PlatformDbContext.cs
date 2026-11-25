using Konnyaku.Application.Tenancy;
using Konnyaku.Infrastructure.Persistence;
using Konnyaku.Infrastructure.Tenancy;
using Konnyaku.Platform.Application;
using Konnyaku.Platform.Contracts;
using Konnyaku.Platform.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Konnyaku.Platform.Infrastructure;

/// <summary>Platform-wide data (tenants, host names): not tenant-owned, so no RLS; always in the central database.</summary>
public sealed class PlatformDbContext(DbContextOptions<PlatformDbContext> options, ITenantContext tenant)
    : ModuleDbContext(options, tenant), IPlatformDbContext
{
    public const string SchemaName = "platform";

    public override string Schema => SchemaName;

    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<TenantDomain> TenantDomains => Set<TenantDomain>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Tenant>(b =>
        {
            b.ToTable("tenant");
            b.HasKey(x => x.Id);
            b.Property(x => x.Slug).HasMaxLength(40);
            b.HasIndex(x => x.Slug).IsUnique();
            b.Property(x => x.Name).HasMaxLength(200);
            b.Property(x => x.DefaultLocale).HasMaxLength(20);
            b.Property(x => x.TimeZone).HasMaxLength(64);
        });
        modelBuilder.Entity<TenantDomain>(b =>
        {
            b.ToTable("tenant_domain");
            b.HasKey(x => x.Id);
            b.Property(x => x.Host).HasMaxLength(253);
            b.HasIndex(x => x.Host).IsUnique();
            b.HasIndex(x => x.TenantId);
        });
    }
}

internal sealed class InProcessPlatformApi(PlatformDbContext db, IMemoryCache cache) : IPlatformApi
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(1);

    public Task<TenantInfo?> ResolveHostAsync(string host, CancellationToken cancellationToken)
    {
        var key = host.Trim().ToLowerInvariant();
        return cache.GetOrCreateAsync($"platform:host:{key}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;
            var tenantId = await db.TenantDomains.AsNoTracking()
                .Where(d => d.Host == key).Select(d => (Guid?)d.TenantId).FirstOrDefaultAsync(cancellationToken);
            return tenantId is { } id ? await LoadAsync(id, cancellationToken) : null;
        });
    }

    public Task<TenantInfo?> GetTenantAsync(Guid tenantId, CancellationToken cancellationToken) =>
        cache.GetOrCreateAsync($"platform:tenant:{tenantId}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;
            return await LoadAsync(tenantId, cancellationToken);
        });

    public async Task<IReadOnlyList<Guid>> ListTenantIdsAsync(CancellationToken cancellationToken) =>
        await db.Tenants.AsNoTracking().OrderBy(t => t.Id).Select(t => t.Id).ToListAsync(cancellationToken);

    private async Task<TenantInfo?> LoadAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
        if (tenant is null)
        {
            return null;
        }

        var hosts = await db.TenantDomains.AsNoTracking()
            .Where(d => d.TenantId == tenantId).OrderBy(d => d.Host).Select(d => d.Host).ToListAsync(cancellationToken);
        return tenant.ToInfo(hosts);
    }
}

public static class PlatformInfrastructure
{
    public static IServiceCollection AddPlatformInfrastructure(this IServiceCollection services, IConfiguration configuration, string connectionName)
    {
        services.AddMemoryCache();
        services.AddModuleDbContext<PlatformDbContext>(configuration, connectionName, PlatformDbContext.SchemaName);
        services.AddScoped<IPlatformDbContext>(sp => sp.GetRequiredService<PlatformDbContext>());
        services.AddScoped<IPlatformApi, InProcessPlatformApi>();
        services.AddSingleton(sp => new PlatformOptions(
            sp.GetRequiredService<IConfiguration>()["Platform:BaseDomain"] ?? "localhost"));
        return services;
    }
}

internal sealed class PlatformDesignTimeFactory : IDesignTimeDbContextFactory<PlatformDbContext>
{
    public PlatformDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNpgsql("Host=localhost;Database=design_time", o => o.MigrationsHistoryTable("__ef_migrations_history", PlatformDbContext.SchemaName))
            .UseSnakeCaseNamingConvention()
            .Options,
        new TenantContext());
}
