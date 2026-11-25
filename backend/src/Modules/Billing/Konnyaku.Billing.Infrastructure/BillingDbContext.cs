using Konnyaku.Application.Tenancy;
using Konnyaku.Billing.Application;
using Konnyaku.Billing.Contracts;
using Konnyaku.Billing.Domain;
using Konnyaku.Infrastructure.Auditing;
using Konnyaku.Infrastructure.Persistence;
using Konnyaku.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Konnyaku.Billing.Infrastructure;

public sealed class BillingDbContext(DbContextOptions<BillingDbContext> options, ITenantContext tenant)
    : ModuleDbContext(options, tenant), IBillingDbContext
{
    public const string SchemaName = "billing";

    public override string Schema => SchemaName;

    public DbSet<Package> Packages => Set<Package>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<CreditLedgerEntry> Ledger => Set<CreditLedgerEntry>();
    public DbSet<CreditBalance> Balances => Set<CreditBalance>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Package>(b =>
        {
            b.ToTable("package");
            b.HasKey(x => x.Id);
            b.Property(x => x.Code).HasMaxLength(30);
            b.Property(x => x.Name).HasConversion(new LocalizedStringConverter(), LocalizedStringConverter.Comparer).HasColumnType("jsonb");
            b.Property(x => x.PaidCredits).HasPrecision(8, 2);
            b.Property(x => x.BonusCredits).HasPrecision(8, 2);
            b.Property(x => x.Price).HasPrecision(18, 2);
            b.Property(x => x.Currency).HasMaxLength(3);
            b.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
        });
        modelBuilder.Entity<Order>(b =>
        {
            b.ToTable("order");
            b.HasKey(x => x.Id);
            b.Property(x => x.Amount).HasPrecision(18, 2);
            b.Property(x => x.AmountReceived).HasPrecision(18, 2);
            b.Property(x => x.PaidCredits).HasPrecision(8, 2);
            b.Property(x => x.BonusCredits).HasPrecision(8, 2);
            b.Property(x => x.Currency).HasMaxLength(3);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Ignore(x => x.UnitAmount);
            b.HasIndex(x => new { x.TenantId, x.StudentId });
        });
        modelBuilder.Entity<Payment>(b =>
        {
            b.ToTable("payment");
            b.HasKey(x => x.Id);
            b.Property(x => x.Amount).HasPrecision(18, 2);
            b.Property(x => x.Method).HasMaxLength(30);
            b.Property(x => x.Reference).HasMaxLength(100);
            b.HasIndex(x => new { x.TenantId, x.OrderId });
        });
        modelBuilder.Entity<CreditLedgerEntry>(b =>
        {
            b.ToTable("credit_ledger_entry");
            b.HasKey(x => x.Id);
            b.Property(x => x.EntryType).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.CreditKind).HasConversion<string>().HasMaxLength(10);
            b.Property(x => x.Credits).HasPrecision(8, 2);
            b.Property(x => x.UnitAmount).HasPrecision(18, 2);
            b.Property(x => x.Currency).HasMaxLength(3);
            b.Property(x => x.Reason).HasMaxLength(300);
            b.HasIndex(x => new { x.TenantId, x.OrderId, x.CreatedAt });
            b.HasIndex(x => new { x.TenantId, x.EntryType, x.SessionLocalDate });
            // One lesson can never be charged twice.
            b.HasIndex(x => x.AttendanceId).IsUnique().HasFilter("attendance_id IS NOT NULL");
        });
        modelBuilder.Entity<CreditBalance>(b =>
        {
            b.ToTable("credit_balance");
            b.HasKey(x => x.OrderId);
            b.Property(x => x.PaidCredits).HasPrecision(8, 2);
            b.Property(x => x.BonusCredits).HasPrecision(8, 2);
            b.Property(x => x.UnitAmount).HasPrecision(18, 2);
            b.Property(x => x.Currency).HasMaxLength(3);
            b.Property(x => x.Version).IsRowVersion();
            b.Ignore(x => x.Total);
            b.HasIndex(x => new { x.TenantId, x.StudentId });
            b.HasIndex(x => x.EnrollmentId).IsUnique().HasFilter("enrollment_id IS NOT NULL");
        });
        modelBuilder.AddChangeLog();
    }
}

public static class BillingInfrastructure
{
    public static IServiceCollection AddBillingInfrastructure(this IServiceCollection services, IConfiguration configuration, string connectionName)
    {
        services.AddModuleDbContext<BillingDbContext>(configuration, connectionName, BillingDbContext.SchemaName);
        services.AddScoped<IBillingDbContext>(sp => sp.GetRequiredService<BillingDbContext>());
        services.AddScoped<BillingQueries>();
        services.AddScoped<IBillingApi>(sp => sp.GetRequiredService<BillingQueries>());
        return services;
    }
}

internal sealed class BillingDesignTimeFactory : IDesignTimeDbContextFactory<BillingDbContext>
{
    public BillingDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<BillingDbContext>()
            .UseNpgsql("Host=localhost;Database=design_time", o => o.MigrationsHistoryTable("__ef_migrations_history", BillingDbContext.SchemaName))
            .UseSnakeCaseNamingConvention()
            .Options,
        new TenantContext());
}
