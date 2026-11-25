using System.Security.Cryptography;
using System.Text;
using Konnyaku.Application.Tenancy;
using Konnyaku.Infrastructure.Persistence;
using Konnyaku.Infrastructure.Tenancy;
using Konnyaku.Platform.Application;
using Konnyaku.Platform.Contracts;
using Konnyaku.Platform.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Konnyaku.Platform.Infrastructure;

/// <summary>Platform-wide data (tenants, host names, operators, audit): not tenant-owned, so no RLS; always in the central database.</summary>
public sealed class PlatformDbContext(DbContextOptions<PlatformDbContext> options, ITenantContext tenant)
    : ModuleDbContext(options, tenant), IPlatformDbContext
{
    public const string SchemaName = "platform";

    public override string Schema => SchemaName;

    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<TenantDomain> TenantDomains => Set<TenantDomain>();

    public DbSet<PlatformOperator> Operators => Set<PlatformOperator>();

    public DbSet<PlatformAuditEntry> AuditEntries => Set<PlatformAuditEntry>();

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
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.SuspendedReason).HasMaxLength(500);
        });
        modelBuilder.Entity<TenantDomain>(b =>
        {
            b.ToTable("tenant_domain");
            b.HasKey(x => x.Id);
            b.Property(x => x.Host).HasMaxLength(253);
            b.HasIndex(x => x.Host).IsUnique();
            b.HasIndex(x => x.TenantId);
        });
        modelBuilder.Entity<PlatformOperator>(b =>
        {
            b.ToTable("operator");
            b.HasKey(x => x.Id);
            b.Property(x => x.Email).HasMaxLength(254);
            b.HasIndex(x => x.Email).IsUnique();
            b.Property(x => x.DisplayName).HasMaxLength(200);
            b.Property(x => x.PasswordHash).HasMaxLength(500);
            b.Property(x => x.TotpSecret).HasMaxLength(500);
            b.Property(x => x.PendingTotpSecret).HasMaxLength(500);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Ignore(x => x.TotpEnrolled);
        });
        modelBuilder.Entity<PlatformAuditEntry>(b =>
        {
            b.ToTable("audit_entry");
            b.HasKey(x => x.Id);
            b.Property(x => x.Actor).HasMaxLength(254);
            b.Property(x => x.Action).HasMaxLength(64);
            b.Property(x => x.Details).HasJsonConversion();
            b.Property(x => x.IpAddress).HasMaxLength(64);
            b.HasIndex(x => x.At);
            b.HasIndex(x => new { x.TenantId, x.At });
        });
    }
}

internal sealed class InProcessPlatformApi(PlatformDbContext db, IMemoryCache cache) : IPlatformApi, ITenantDirectoryCache
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(1);

    public Task<TenantInfo?> ResolveHostAsync(string host, CancellationToken cancellationToken)
    {
        var key = TenantDomain.NormalizeHost(host);
        return cache.GetOrCreateAsync(HostKey(key), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;
            var tenantId = await db.TenantDomains.AsNoTracking()
                .Where(d => d.Host == key).Select(d => (Guid?)d.TenantId).FirstOrDefaultAsync(cancellationToken);
            return tenantId is { } id ? await LoadAsync(id, cancellationToken) : null;
        });
    }

    public Task<TenantInfo?> GetTenantAsync(Guid tenantId, CancellationToken cancellationToken) =>
        cache.GetOrCreateAsync(TenantKey(tenantId), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;
            return await LoadAsync(tenantId, cancellationToken);
        });

    /// <summary>
    /// Drops this API instance's cache. Other instances (several API replicas) see the change within
    /// <see cref="CacheDuration"/>; a suspension therefore takes effect everywhere within a minute.
    /// </summary>
    public void Invalidate(Guid tenantId, IEnumerable<string> hosts)
    {
        cache.Remove(TenantKey(tenantId));
        foreach (var host in hosts)
        {
            cache.Remove(HostKey(TenantDomain.NormalizeHost(host)));
        }
    }

    private static string HostKey(string host) => $"platform:host:{host}";

    private static string TenantKey(Guid tenantId) => $"platform:tenant:{tenantId}";

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

/// <summary>AES-GCM with a key derived from <c>Platform:SecretKey</c> (or, if unset, <c>Auth:SigningKey</c>).</summary>
internal sealed class AesSecretProtector : ISecretProtector
{
    private const string Version = "v1";
    private readonly byte[] _key;

    public AesSecretProtector(IConfiguration configuration)
    {
        var material = configuration["Platform:SecretKey"] is { Length: > 0 } own ? own : configuration["Auth:SigningKey"];
        if (string.IsNullOrEmpty(material) || Encoding.UTF8.GetByteCount(material) < 32)
        {
            throw new InvalidOperationException("Platform:SecretKey or Auth:SigningKey must be configured with at least 32 bytes.");
        }

        _key = HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(material), 32, info: Encoding.UTF8.GetBytes("konnyaku-platform-secrets"));
    }

    public string Protect(byte[] secret)
    {
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var cipher = new byte[secret.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        using var aes = new AesGcm(_key, tag.Length);
        aes.Encrypt(nonce, secret, cipher, tag);
        return $"{Version}.{Convert.ToBase64String(nonce)}.{Convert.ToBase64String(cipher)}.{Convert.ToBase64String(tag)}";
    }

    public byte[] Unprotect(string protectedSecret)
    {
        var parts = protectedSecret.Split('.');
        if (parts.Length != 4 || parts[0] != Version)
        {
            throw new CryptographicException("Unknown protected secret format.");
        }

        var (nonce, cipher, tag) = (Convert.FromBase64String(parts[1]), Convert.FromBase64String(parts[2]), Convert.FromBase64String(parts[3]));
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(_key, tag.Length);
        aes.Decrypt(nonce, cipher, tag, plain);
        return plain;
    }
}

internal sealed class TenantScope(IServiceScopeFactory scopes) : ITenantScope
{
    public async Task<T> RunAsync<T>(Guid tenantId, Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Set(tenantId);
        return await work(scope.ServiceProvider);
    }
}

public static class PlatformInfrastructure
{
    public static IServiceCollection AddPlatformInfrastructure(this IServiceCollection services, IConfiguration configuration, string connectionName)
    {
        services.AddMemoryCache();
        services.AddModuleDbContext<PlatformDbContext>(configuration, connectionName, PlatformDbContext.SchemaName);
        services.AddScoped<IPlatformDbContext>(sp => sp.GetRequiredService<PlatformDbContext>());
        services.AddScoped<InProcessPlatformApi>();
        services.AddScoped<IPlatformApi>(sp => sp.GetRequiredService<InProcessPlatformApi>());
        services.AddScoped<ITenantDirectoryCache>(sp => sp.GetRequiredService<InProcessPlatformApi>());
        services.AddSingleton<ISecretProtector, AesSecretProtector>();
        services.AddSingleton<IPasswordHasher<PlatformOperator>, PasswordHasher<PlatformOperator>>();
        services.AddSingleton<ITenantScope, TenantScope>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            var baseDomain = TenantDomain.NormalizeHost(config["Platform:BaseDomain"] ?? "localhost");
            var adminHost = config["Platform:AdminHost"] is { Length: > 0 } host ? TenantDomain.NormalizeHost(host) : $"admin.{baseDomain}";
            return new PlatformOptions(baseDomain, adminHost);
        });
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
