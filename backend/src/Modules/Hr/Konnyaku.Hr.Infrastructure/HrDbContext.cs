using Konnyaku.Application.Tenancy;
using Konnyaku.Hr.Application;
using Konnyaku.Hr.Domain;
using Konnyaku.Infrastructure.Auditing;
using Konnyaku.Infrastructure.Persistence;
using Konnyaku.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Konnyaku.Hr.Infrastructure;

public sealed class HrDbContext(DbContextOptions<HrDbContext> options, ITenantContext tenant)
    : ModuleDbContext(options, tenant), IHrDbContext
{
    public const string SchemaName = "hr";

    public override string Schema => SchemaName;

    public DbSet<PayRate> PayRates => Set<PayRate>();
    public DbSet<Timesheet> Timesheets => Set<Timesheet>();
    public DbSet<PayrollPeriod> PayrollPeriods => Set<PayrollPeriod>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PayRate>(b =>
        {
            b.ToTable("pay_rate");
            b.HasKey(x => x.Id);
            b.Property(x => x.HourlyRate).HasPrecision(18, 2);
            b.Property(x => x.Currency).HasMaxLength(3);
            b.HasIndex(x => new { x.TenantId, x.TeacherId, x.EffectiveFrom });
        });
        modelBuilder.Entity<Timesheet>(b =>
        {
            b.ToTable("timesheet");
            b.HasKey(x => x.Id);
            b.Property(x => x.HourlyRate).HasPrecision(18, 2);
            b.Property(x => x.Amount).HasPrecision(18, 2);
            b.Property(x => x.Currency).HasMaxLength(3);
            b.Property(x => x.Source).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Ignore(x => x.PaidMinutes);
            b.HasIndex(x => new { x.SessionId, x.TeacherId }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.TeacherId, x.LocalDate });
            b.HasIndex(x => new { x.TenantId, x.Status, x.LocalDate });
        });
        modelBuilder.Entity<PayrollPeriod>(b =>
        {
            b.ToTable("payroll_period");
            b.HasKey(x => x.Id);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.HasIndex(x => new { x.TenantId, x.From });
        });
        modelBuilder.AddChangeLog();
    }
}

public static class HrInfrastructure
{
    public static IServiceCollection AddHrInfrastructure(this IServiceCollection services, IConfiguration configuration, string connectionName)
    {
        services.AddModuleDbContext<HrDbContext>(configuration, connectionName, HrDbContext.SchemaName);
        services.AddScoped<IHrDbContext>(sp => sp.GetRequiredService<HrDbContext>());
        return services;
    }
}

internal sealed class HrDesignTimeFactory : IDesignTimeDbContextFactory<HrDbContext>
{
    public HrDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<HrDbContext>()
            .UseNpgsql("Host=localhost;Database=design_time", o => o.MigrationsHistoryTable("__ef_migrations_history", HrDbContext.SchemaName))
            .UseSnakeCaseNamingConvention()
            .Options,
        new TenantContext());
}
