using Konnyaku.Application.Tenancy;
using Konnyaku.Crm.Application;
using Konnyaku.Crm.Contracts;
using Konnyaku.Crm.Domain;
using Konnyaku.Infrastructure.Persistence;
using Konnyaku.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Konnyaku.Crm.Infrastructure;

public sealed class CrmDbContext(DbContextOptions<CrmDbContext> options, ITenantContext tenant)
    : ModuleDbContext(options, tenant), ICrmDbContext
{
    public const string SchemaName = "crm";

    public override string Schema => SchemaName;

    public DbSet<Lead> Leads => Set<Lead>();

    public DbSet<StudentProfile> Students => Set<StudentProfile>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Lead>(b =>
        {
            b.ToTable("lead");
            b.HasKey(x => x.Id);
            b.Property(x => x.StudentName).HasMaxLength(200);
            b.Property(x => x.GuardianName).HasMaxLength(200);
            b.Property(x => x.Phone).HasMaxLength(20);
            b.Property(x => x.Email).HasMaxLength(320);
            b.Property(x => x.LanguageCode).HasMaxLength(20);
            b.Property(x => x.Goal).HasMaxLength(500);
            b.Property(x => x.PreferredSchedule).HasMaxLength(500);
            b.Property(x => x.Source).HasMaxLength(40);
            b.Property(x => x.Utm).HasJsonConversion();
            b.Property(x => x.ConsentVersion).HasMaxLength(40);
            b.Property(x => x.Locale).HasMaxLength(20);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Ignore(x => x.IsOpen);
            b.HasIndex(x => new { x.TenantId, x.Phone });
            b.HasIndex(x => new { x.TenantId, x.Email });
            b.HasIndex(x => new { x.TenantId, x.Status, x.FollowUpDueAt });
        });
        modelBuilder.Entity<StudentProfile>(b =>
        {
            b.ToTable("student_profile");
            b.HasKey(x => x.Id);
            b.Property(x => x.FullName).HasMaxLength(200);
            b.Property(x => x.GuardianName).HasMaxLength(200);
            b.Property(x => x.Phone).HasMaxLength(20);
            b.Property(x => x.Email).HasMaxLength(320);
            b.Property(x => x.CommunicationLocale).HasMaxLength(20);
            b.HasIndex(x => new { x.TenantId, x.UserId }).IsUnique().HasFilter("user_id IS NOT NULL");
        });
    }
}

public static class CrmInfrastructure
{
    public static IServiceCollection AddCrmInfrastructure(this IServiceCollection services, IConfiguration configuration, string connectionName)
    {
        services.AddModuleDbContext<CrmDbContext>(configuration, connectionName, CrmDbContext.SchemaName);
        services.AddScoped<ICrmDbContext>(sp => sp.GetRequiredService<CrmDbContext>());
        services.AddScoped<CrmQueries>();
        services.AddScoped<ICrmApi>(sp => sp.GetRequiredService<CrmQueries>());
        services.AddScoped<ICrmUsageApi, CrmUsageQueries>();
        services.AddSingleton(TimeProvider.System);
        return services;
    }
}

internal sealed class CrmDesignTimeFactory : IDesignTimeDbContextFactory<CrmDbContext>
{
    public CrmDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<CrmDbContext>()
            .UseNpgsql("Host=localhost;Database=design_time", o => o.MigrationsHistoryTable("__ef_migrations_history", CrmDbContext.SchemaName))
            .UseSnakeCaseNamingConvention()
            .Options,
        new TenantContext());
}
