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
    public DbSet<SessionSeat> Seats => Set<SessionSeat>();
    public DbSet<SessionBooking> SessionBookings => Set<SessionBooking>();
    public DbSet<MakeupEntitlement> Entitlements => Set<MakeupEntitlement>();
    public DbSet<SessionWaitlistEntry> Waitlist => Set<SessionWaitlistEntry>();
    public DbSet<BookingSettings> Settings => Set<BookingSettings>();

    public async Task InsertSeatIfMissingAsync(SessionSeat seat, CancellationToken cancellationToken)
    {
        var tenantId = CurrentTenantId ?? throw new TenantContextMissingException();
        await Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO scheduling.session_seat
                (id, tenant_id, session_id, online_capacity, offline_capacity, online_booked, offline_booked, makeup_capacity, makeup_booked)
            VALUES ({seat.Id}, {tenantId}, {seat.SessionId}, {seat.OnlineCapacity}, {seat.OfflineCapacity}, 0, 0, {seat.MakeupCapacity}, 0)
            ON CONFLICT (tenant_id, session_id) DO NOTHING
            """, cancellationToken);
    }

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
        modelBuilder.Entity<SessionSeat>(b =>
        {
            b.ToTable("session_seat", t =>
            {
                t.HasCheckConstraint("ck_session_seat_counts", "online_booked >= 0 AND offline_booked >= 0 AND makeup_booked >= 0");
            });
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.TenantId, x.SessionId }).IsUnique();
        });
        modelBuilder.Entity<SessionBooking>(b =>
        {
            b.ToTable("session_booking");
            b.HasKey(x => x.Id);
            b.Property(x => x.Mode).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Version).IsRowVersion();
            b.Ignore(x => x.HoldsSeat);
            // One row per learner and session: postponing, cancelling and booking again change the same row.
            b.HasIndex(x => new { x.TenantId, x.SessionId, x.StudentId }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.StudentId, x.Status });
            b.HasIndex(x => new { x.TenantId, x.EnrollmentId, x.Status });
        });
        modelBuilder.Entity<MakeupEntitlement>(b =>
        {
            b.ToTable("makeup_entitlement");
            b.HasKey(x => x.Id);
            b.Property(x => x.RequestNote).HasMaxLength(300);
            b.Property(x => x.Version).IsRowVersion();
            b.HasIndex(x => new { x.TenantId, x.StudentId, x.ExpiresAt });
            b.HasIndex(x => new { x.TenantId, x.SourceBookingId }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.RequestedAt });
        });
        modelBuilder.Entity<SessionWaitlistEntry>(b =>
        {
            b.ToTable("session_waitlist");
            b.HasKey(x => x.Id);
            b.Property(x => x.Mode).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Version).IsRowVersion();
            b.Ignore(x => x.IsActive);
            b.HasIndex(x => new { x.TenantId, x.SessionId, x.StudentId }).IsUnique().HasFilter("status IN ('Waiting', 'Offered')");
            b.HasIndex(x => new { x.TenantId, x.SessionId, x.Status, x.CreatedAt });
            b.HasIndex(x => new { x.Status, x.OfferExpiresAt });
        });
        modelBuilder.Entity<BookingSettings>(b =>
        {
            b.ToTable("booking_settings");
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.TenantId).IsUnique();
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
        services.AddScoped<BookingSettingsStore>();
        services.AddScoped<SeatLedger>();
        services.AddScoped<BookingSync>();
        services.AddScoped<WaitlistOffers>();
        services.AddScoped<BookingQueries>();
        services.AddScoped<LearnerBookingHandlers>();
        services.AddScoped<MakeupAdminHandlers>();
        return services;
    }

    /// <summary>Worker only: expires seat offers of the waitlist and passes them on.</summary>
    public static IServiceCollection AddSchedulingWorker(this IServiceCollection services)
    {
        services.AddSingleton<WaitlistExpiryService>();
        services.AddHostedService(sp => sp.GetRequiredService<WaitlistExpiryService>());
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
