using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using Konnyaku.Application.Tenancy;
using Konnyaku.Content.Application;
using Konnyaku.Content.Domain;
using Konnyaku.Infrastructure.Persistence;
using Konnyaku.Infrastructure.Storage;
using Konnyaku.Infrastructure.Tenancy;
using Konnyaku.Platform.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Konnyaku.Content.Infrastructure;

public sealed class ContentDbContext(DbContextOptions<ContentDbContext> options, ITenantContext tenant)
    : ModuleDbContext(options, tenant), IContentDbContext
{
    public const string SchemaName = "content";

    public override string Schema => SchemaName;

    public DbSet<StoredFile> Files => Set<StoredFile>();
    public DbSet<TenantQuota> Quotas => Set<TenantQuota>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StoredFile>(b =>
        {
            b.ToTable("stored_file");
            b.HasKey(x => x.Id);
            b.Property(x => x.OwnerKind).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Purpose).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.FileName).HasMaxLength(StoredFile.MaxFileNameLength);
            b.Property(x => x.ContentType).HasMaxLength(100);
            b.Property(x => x.Sha256).HasMaxLength(64);
            b.Property(x => x.ObjectKey).HasMaxLength(200);
            b.Property(x => x.StatusReason).HasMaxLength(200);
            b.Ignore(x => x.CountsAgainstQuota);
            b.HasIndex(x => x.ObjectKey).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.Status, x.CreatedAt });
            b.HasIndex(x => new { x.TenantId, x.OwnerUserId, x.CreatedAt });
        });
        modelBuilder.Entity<TenantQuota>(b =>
        {
            b.ToTable("tenant_quota");
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.TenantId).IsUnique();
        });
    }
}

public static class ContentInfrastructure
{
    public static IServiceCollection AddContentInfrastructure(this IServiceCollection services, IConfiguration configuration, string connectionName)
    {
        services.AddModuleDbContext<ContentDbContext>(configuration, connectionName, ContentDbContext.SchemaName);
        services.AddScoped<IContentDbContext>(sp => sp.GetRequiredService<ContentDbContext>());
        services.Configure<ContentOptions>(configuration.GetSection(ContentOptions.Section));
        services.AddObjectStorage(configuration);
        services.AddSingleton<IVirusScanner, ClamAvScanner>();
        return services;
    }

    public static IServiceCollection AddContentWorker(this IServiceCollection services)
    {
        services.AddSingleton<PendingUploadCleanupService>();
        services.AddHostedService(sp => sp.GetRequiredService<PendingUploadCleanupService>());
        return services;
    }
}

/// <summary>
/// ClamAV over its TCP protocol (INSTREAM), when <c>Content:ClamAv:Host</c> is set (compose profile <c>clamav</c>);
/// otherwise disabled. The file is streamed in chunks, never held in memory.
/// </summary>
public sealed class ClamAvScanner(IOptions<ContentOptions> options) : IVirusScanner
{
    private readonly ClamAvOptions _options = options.Value.ClamAv;

    public bool Enabled => !string.IsNullOrWhiteSpace(_options.Host);

    public async Task<ScanResult> ScanAsync(Stream content, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
        var ct = timeout.Token;

        using var client = new TcpClient();
        await client.ConnectAsync(_options.Host!, _options.Port, ct);
        await using var network = client.GetStream();
        await network.WriteAsync("zINSTREAM\0"u8.ToArray(), ct);

        var buffer = new byte[64 * 1024];
        var size = new byte[4];
        int read;
        while ((read = await content.ReadAsync(buffer, ct)) > 0)
        {
            BinaryPrimitives.WriteUInt32BigEndian(size, (uint)read);
            await network.WriteAsync(size, ct);
            await network.WriteAsync(buffer.AsMemory(0, read), ct);
        }

        await network.WriteAsync(new byte[4], ct);

        using var reply = new MemoryStream();
        while ((read = await network.ReadAsync(buffer, ct)) > 0)
        {
            reply.Write(buffer, 0, read);
            if (buffer[read - 1] == 0)
            {
                break;
            }
        }

        // "stream: OK", "stream: Eicar-Signature FOUND", or "... ERROR".
        var text = Encoding.ASCII.GetString(reply.ToArray()).TrimEnd('\0', '\n');
        if (text.EndsWith(" OK", StringComparison.Ordinal))
        {
            return new ScanResult(true, null);
        }

        if (text.EndsWith(" FOUND", StringComparison.Ordinal))
        {
            var finding = text["stream: ".Length..^" FOUND".Length];
            return new ScanResult(false, finding);
        }

        throw new InvalidOperationException($"ClamAV error: {text}");
    }
}

/// <summary>
/// Worker job: every hour, tenant by tenant (each in its own scope and tenant context, so RLS applies), removes the
/// uploads left Pending past <c>Content:PendingTtlHours</c> (object and record).
/// </summary>
public sealed class PendingUploadCleanupService(IServiceScopeFactory scopeFactory, ILogger<PendingUploadCleanupService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Pending upload cleanup failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid> tenants;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            tenants = await scope.ServiceProvider.GetRequiredService<IPlatformApi>().ListTenantIdsAsync(cancellationToken);
        }

        var total = 0;
        foreach (var tenantId in tenants)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().Set(tenantId);
            var count = await scope.ServiceProvider.GetRequiredService<ContentHandlers>().CleanupPendingAsync(cancellationToken);
            if (count > 0)
            {
                logger.LogInformation("Removed {Count} unfinished uploads of tenant {TenantId}", count, tenantId);
            }

            total += count;
        }

        return total;
    }
}

internal sealed class ContentDesignTimeFactory : IDesignTimeDbContextFactory<ContentDbContext>
{
    public ContentDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<ContentDbContext>()
            .UseNpgsql("Host=localhost;Database=design_time", o => o.MigrationsHistoryTable("__ef_migrations_history", ContentDbContext.SchemaName))
            .UseSnakeCaseNamingConvention()
            .Options,
        new TenantContext());
}
