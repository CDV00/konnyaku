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
    public DbSet<Refund> Refunds => Set<Refund>();
    public DbSet<ReceiptPrintLog> ReceiptPrints => Set<ReceiptPrintLog>();

    /// <summary>Next number of a document kind for the current tenant; call inside the transaction that saves the document.</summary>
    public async Task<long> NextDocumentNumberAsync(string kind, CancellationToken cancellationToken)
    {
        var tenantId = CurrentTenantId ?? throw new Konnyaku.Application.Tenancy.TenantContextMissingException();
        var numbers = await Database.SqlQuery<long>($"""
            INSERT INTO billing.document_sequence (tenant_id, kind, last_value) VALUES ({tenantId}, {kind}, 1)
            ON CONFLICT (tenant_id, kind) DO UPDATE SET last_value = billing.document_sequence.last_value + 1
            RETURNING last_value AS "Value"
            """).ToListAsync(cancellationToken);
        return numbers.Single();
    }

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
            b.Property(x => x.AmountRefunded).HasPrecision(18, 2);
            b.Property(x => x.PaidCredits).HasPrecision(8, 2);
            b.Property(x => x.BonusCredits).HasPrecision(8, 2);
            b.Property(x => x.Currency).HasMaxLength(3);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Ignore(x => x.UnitAmount);
            b.Ignore(x => x.RefundKind);
            b.HasIndex(x => new { x.TenantId, x.StudentId });
        });
        modelBuilder.Entity<Payment>(b =>
        {
            b.ToTable("payment");
            b.HasKey(x => x.Id);
            b.Property(x => x.Amount).HasPrecision(18, 2);
            b.Property(x => x.Method).HasMaxLength(30);
            b.Property(x => x.Reference).HasMaxLength(100);
            b.Property(x => x.ReceivedByName).HasMaxLength(200);
            b.HasIndex(x => new { x.TenantId, x.OrderId });
            b.HasIndex(x => new { x.TenantId, x.ReceiptNo }).IsUnique();
        });
        modelBuilder.Entity<Refund>(b =>
        {
            b.ToTable("refund");
            b.HasKey(x => x.Id);
            b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Amount).HasPrecision(18, 2);
            b.Property(x => x.SuggestedAmount).HasPrecision(18, 2);
            b.Property(x => x.UnusedValue).HasPrecision(18, 2);
            b.Property(x => x.RetainedAmount).HasPrecision(18, 2);
            b.Property(x => x.PaidCreditsReturned).HasPrecision(8, 2);
            b.Property(x => x.BonusCreditsForfeited).HasPrecision(8, 2);
            b.Property(x => x.Currency).HasMaxLength(3);
            b.Property(x => x.Method).HasMaxLength(30);
            b.Property(x => x.Reference).HasMaxLength(100);
            b.Property(x => x.Reason).HasMaxLength(300);
            b.Property(x => x.ApprovedByName).HasMaxLength(200);
            // One refund closes an order: a second one (even concurrent) is refused by the database.
            b.HasIndex(x => new { x.TenantId, x.OrderId }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.ReceiptNo }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.LocalDate });
        });
        modelBuilder.Entity<DocumentSequence>(b =>
        {
            b.ToTable("document_sequence");
            b.HasKey(x => new { x.TenantId, x.Kind });
            b.Property(x => x.Kind).HasMaxLength(20);
        });
        modelBuilder.Entity<ReceiptPrintLog>(b =>
        {
            b.ToTable("receipt_print_log");
            b.HasKey(x => x.Id);
            b.Property(x => x.DocumentKind).HasMaxLength(20);
            b.Property(x => x.Action).HasMaxLength(20);
            b.Property(x => x.Audience).HasMaxLength(10);
            b.Property(x => x.ActorName).HasMaxLength(200);
            b.Property(x => x.ClientIp).HasMaxLength(64);
            b.HasIndex(x => new { x.TenantId, x.DocumentId, x.OccurredAt });
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
        services.AddSingleton<IReceiptPdfRenderer, Receipts.ReceiptPdfRenderer>();
        services.AddSingleton(new ReceiptSettings(configuration.GetValue("Billing:ReceiptPrintsPerDay", 20)));
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
