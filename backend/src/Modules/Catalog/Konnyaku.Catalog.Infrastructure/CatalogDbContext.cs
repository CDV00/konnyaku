using Konnyaku.Application.Tenancy;
using Konnyaku.Catalog.Application;
using Konnyaku.Catalog.Contracts;
using Konnyaku.Catalog.Domain;
using Konnyaku.Infrastructure.Persistence;
using Konnyaku.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Konnyaku.Catalog.Infrastructure;

/// <summary>Platform reference data, read-only for tenants and identical in every database.</summary>
public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options, ITenantContext tenant)
    : ModuleDbContext(options, tenant), ICatalogDbContext
{
    public const string SchemaName = "catalog";

    public override string Schema => SchemaName;

    public DbSet<Language> Languages => Set<Language>();

    public DbSet<ProficiencyFramework> Frameworks => Set<ProficiencyFramework>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        var converter = new LocalizedStringConverter();
        modelBuilder.Entity<Language>(b =>
        {
            b.ToTable("language");
            b.HasKey(x => x.Code);
            b.Property(x => x.Code).HasMaxLength(20);
            b.Property(x => x.Name).HasConversion(converter, LocalizedStringConverter.Comparer).HasColumnType("jsonb");
            b.HasData(CatalogSeed.Languages);
        });
        modelBuilder.Entity<ProficiencyFramework>(b =>
        {
            b.ToTable("proficiency_framework");
            b.HasKey(x => x.Code);
            b.Property(x => x.Code).HasMaxLength(30);
            b.Property(x => x.LanguageCode).HasMaxLength(20);
            b.Property(x => x.Name).HasConversion(converter, LocalizedStringConverter.Comparer).HasColumnType("jsonb");
            b.HasMany(x => x.Levels).WithOne().HasForeignKey(x => x.FrameworkCode);
            b.HasMany(x => x.Skills).WithOne().HasForeignKey(x => x.FrameworkCode);
            b.HasData(CatalogSeed.Frameworks);
        });
        modelBuilder.Entity<ProficiencyLevel>(b =>
        {
            b.ToTable("proficiency_level");
            b.HasKey(x => new { x.FrameworkCode, x.Code });
            b.Property(x => x.Code).HasMaxLength(20);
            b.Property(x => x.CefrApprox).HasMaxLength(10);
            b.HasData(CatalogSeed.Levels);
        });
        modelBuilder.Entity<SkillDimension>(b =>
        {
            b.ToTable("skill_dimension");
            b.HasKey(x => new { x.FrameworkCode, x.Code });
            b.Property(x => x.Code).HasMaxLength(30);
            b.Property(x => x.Name).HasConversion(converter, LocalizedStringConverter.Comparer).HasColumnType("jsonb");
            b.HasData(CatalogSeed.Skills);
        });
    }
}

public static class CatalogInfrastructure
{
    public static IServiceCollection AddCatalogInfrastructure(this IServiceCollection services, IConfiguration configuration, string connectionName)
    {
        services.AddModuleDbContext<CatalogDbContext>(configuration, connectionName, CatalogDbContext.SchemaName);
        services.AddScoped<ICatalogDbContext>(sp => sp.GetRequiredService<CatalogDbContext>());
        services.AddScoped<ICatalogApi, CatalogQueries>();
        return services;
    }
}

internal sealed class CatalogDesignTimeFactory : IDesignTimeDbContextFactory<CatalogDbContext>
{
    public CatalogDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql("Host=localhost;Database=design_time", o => o.MigrationsHistoryTable("__ef_migrations_history", CatalogDbContext.SchemaName))
            .UseSnakeCaseNamingConvention()
            .Options,
        new TenantContext());
}
