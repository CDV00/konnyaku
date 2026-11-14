using Konnyaku.Application.Tenancy;
using Konnyaku.Infrastructure.Persistence;
using Konnyaku.Infrastructure.Tenancy;
using Konnyaku.Workspace.Application;
using Konnyaku.Workspace.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Konnyaku.Workspace.Infrastructure;

public sealed class WorkspaceDbContext(DbContextOptions<WorkspaceDbContext> options, ITenantContext tenant)
    : ModuleDbContext(options, tenant), IWorkspaceDbContext
{
    public const string SchemaName = "workspace";

    public override string Schema => SchemaName;

    public DbSet<WorkspaceConnection> Connections => Set<WorkspaceConnection>();
    public DbSet<HostAccount> Hosts => Set<HostAccount>();
    public DbSet<HostLease> Leases => Set<HostLease>();
    public DbSet<Recording> Recordings => Set<Recording>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WorkspaceConnection>(b =>
        {
            b.ToTable("workspace_connection");
            b.HasKey(x => x.Id);
            b.Property(x => x.Provider).HasMaxLength(20);
            b.Property(x => x.Domain).HasMaxLength(253);
            b.HasIndex(x => x.TenantId).IsUnique();
        });
        modelBuilder.Entity<HostAccount>(b =>
        {
            b.ToTable("host_account");
            b.HasKey(x => x.Id);
            b.Property(x => x.Email).HasMaxLength(320);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.HasIndex(x => new { x.TenantId, x.Email }).IsUnique();
        });
        modelBuilder.Entity<HostLease>(b =>
        {
            b.ToTable("host_lease");
            b.HasKey(x => x.Id);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.SpaceName).HasMaxLength(200);
            b.Property(x => x.MeetingUri).HasMaxLength(500);
            b.Ignore(x => x.IsLive);
            b.HasIndex(x => new { x.TenantId, x.SessionId });
            b.HasIndex(x => new { x.TenantId, x.HostAccountId, x.StartsAt });
        });
        modelBuilder.Entity<Recording>(b =>
        {
            b.ToTable("recording");
            b.HasKey(x => x.Id);
            b.Property(x => x.FileRef).HasMaxLength(500);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.HasIndex(x => new { x.TenantId, x.SessionId });
        });
    }
}

/// <summary>Development/CI provider: deterministic meeting links, no external calls.</summary>
public sealed class FakeVirtualRoomProvider : IVirtualRoomProvider
{
    public string Name => Providers.Fake;

    public Task<CreatedSpace> CreateSpaceAsync(string hostEmail, Guid sessionId, DateTimeOffset startsAt, DateTimeOffset endsAt, CancellationToken cancellationToken)
    {
        // The random tail of the UUIDv7 (its head is a timestamp, shared by sessions created together).
        var code = sessionId.ToString("N")[^10..];
        // Relative link: opens the simulated meeting page of the tenant site (frontend/apps/site/app/dev-meeting).
        return Task.FromResult(new CreatedSpace($"spaces/{code}", $"/dev-meeting/{code[..3]}-{code[3..7]}-{code[7..]}"));
    }

    public Task GrantRecordingAccessAsync(string fileRef, IReadOnlyCollection<Guid> viewerUserIds, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

internal sealed class VirtualRoomProviders(IEnumerable<IVirtualRoomProvider> providers) : IVirtualRoomProviders
{
    public IVirtualRoomProvider? Find(string providerName) => providers.FirstOrDefault(p => p.Name == providerName);
}

public static class WorkspaceInfrastructure
{
    public static IServiceCollection AddWorkspaceInfrastructure(this IServiceCollection services, IConfiguration configuration, string connectionName)
    {
        services.AddModuleDbContext<WorkspaceDbContext>(configuration, connectionName, WorkspaceDbContext.SchemaName);
        services.AddScoped<IWorkspaceDbContext>(sp => sp.GetRequiredService<WorkspaceDbContext>());
        // The Google Meet provider plugs in here once a test Workspace domain is available (docs 2.3.5).
        services.AddSingleton<IVirtualRoomProvider, FakeVirtualRoomProvider>();
        services.AddSingleton<IVirtualRoomProviders, VirtualRoomProviders>();
        return services;
    }
}

internal sealed class WorkspaceDesignTimeFactory : IDesignTimeDbContextFactory<WorkspaceDbContext>
{
    public WorkspaceDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<WorkspaceDbContext>()
            .UseNpgsql("Host=localhost;Database=design_time", o => o.MigrationsHistoryTable("__ef_migrations_history", WorkspaceDbContext.SchemaName))
            .UseSnakeCaseNamingConvention()
            .Options,
        new TenantContext());
}
