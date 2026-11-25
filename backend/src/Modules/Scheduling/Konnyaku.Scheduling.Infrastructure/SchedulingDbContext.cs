using Konnyaku.Application.Tenancy;
using Konnyaku.Infrastructure.Auditing;
using Konnyaku.Infrastructure.Persistence;
using Konnyaku.Infrastructure.Tenancy;
using Konnyaku.Scheduling.Application;
using Konnyaku.Scheduling.Contracts;
using Konnyaku.Scheduling.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Konnyaku.Scheduling.Infrastructure;

public sealed class SchedulingDbContext(DbContextOptions<SchedulingDbContext> options, ITenantContext tenant)
    : ModuleDbContext(options, tenant), ISchedulingDbContext
{
    public const string SchemaName = "scheduling";

    public override string Schema => SchemaName;

    public DbSet<SchedulePattern> Patterns => Set<SchedulePattern>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<ResourceBooking> Bookings => Set<ResourceBooking>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SchedulePattern>(b =>
        {
            b.ToTable("schedule_pattern");
            b.HasKey(x => x.Id);
            b.Property(x => x.TimeZone).HasMaxLength(64);
            b.HasIndex(x => new { x.TenantId, x.ClassId });
        });
        modelBuilder.Entity<Session>(b =>
        {
            b.ToTable("session");
            b.HasKey(x => x.Id);
            b.Property(x => x.TimeZone).HasMaxLength(64);
            b.Property(x => x.DeliveryMode).HasMaxLength(20);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.CancelReason).HasMaxLength(200);
            b.Property(x => x.Version).IsRowVersion();
            b.Ignore(x => x.IsChangeable);
            b.HasIndex(x => new { x.TenantId, x.ClassId, x.StartsAt });
            b.HasIndex(x => new { x.TenantId, x.ActualTeacherId, x.StartsAt });
            b.HasIndex(x => new { x.Status, x.StartsAt });
        });
        modelBuilder.Entity<ResourceBooking>(b =>
        {
            b.ToTable("resource_booking");
            b.HasKey(x => x.Id);
            b.Property(x => x.ResourceType).HasMaxLength(20);
            b.HasIndex(x => new { x.TenantId, x.SessionId });
        });
        modelBuilder.AddChangeLog();
    }
}

public static class SchedulingInfrastructure
{
    public static IServiceCollection AddSchedulingInfrastructure(this IServiceCollection services, IConfiguration configuration, string connectionName)
    {
        services.AddModuleDbContext<SchedulingDbContext>(configuration, connectionName, SchedulingDbContext.SchemaName);
        services.AddScoped<ISchedulingDbContext>(sp => sp.GetRequiredService<SchedulingDbContext>());
        services.AddScoped<SchedulingQueries>();
        services.AddScoped<ISchedulingApi>(sp => sp.GetRequiredService<SchedulingQueries>());
        return services;
    }
}

internal sealed class SchedulingDesignTimeFactory : IDesignTimeDbContextFactory<SchedulingDbContext>
{
    public SchedulingDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<SchedulingDbContext>()
            .UseNpgsql("Host=localhost;Database=design_time", o => o.MigrationsHistoryTable("__ef_migrations_history", SchedulingDbContext.SchemaName))
            .UseSnakeCaseNamingConvention()
            .Options,
        new TenantContext());
}
