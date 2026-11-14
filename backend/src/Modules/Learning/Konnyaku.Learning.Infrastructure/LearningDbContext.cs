using Konnyaku.Application.Tenancy;
using Konnyaku.Infrastructure.Persistence;
using Konnyaku.Infrastructure.Tenancy;
using Konnyaku.Learning.Application;
using Konnyaku.Learning.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Konnyaku.Learning.Infrastructure;

public sealed class LearningDbContext(DbContextOptions<LearningDbContext> options, ITenantContext tenant)
    : ModuleDbContext(options, tenant), ILearningDbContext
{
    public const string SchemaName = "learning";

    public override string Schema => SchemaName;

    public DbSet<Attendance> Attendances => Set<Attendance>();

    public DbSet<RecordingAccess> RecordingAccesses => Set<RecordingAccess>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Attendance>(b =>
        {
            b.ToTable("attendance");
            b.HasKey(x => x.Id);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Source).HasConversion<string>().HasMaxLength(20);
            b.Ignore(x => x.IsConfirmed);
            b.HasIndex(x => new { x.SessionId, x.EnrollmentId }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.EnrollmentId });
        });
        modelBuilder.Entity<RecordingAccess>(b =>
        {
            b.ToTable("recording_access");
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.RecordingId, x.UserId }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.UserId });
        });
    }
}

public static class LearningInfrastructure
{
    public static IServiceCollection AddLearningInfrastructure(this IServiceCollection services, IConfiguration configuration, string connectionName)
    {
        services.AddModuleDbContext<LearningDbContext>(configuration, connectionName, LearningDbContext.SchemaName);
        services.AddScoped<ILearningDbContext>(sp => sp.GetRequiredService<LearningDbContext>());
        return services;
    }
}

internal sealed class LearningDesignTimeFactory : IDesignTimeDbContextFactory<LearningDbContext>
{
    public LearningDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<LearningDbContext>()
            .UseNpgsql("Host=localhost;Database=design_time", o => o.MigrationsHistoryTable("__ef_migrations_history", LearningDbContext.SchemaName))
            .UseSnakeCaseNamingConvention()
            .Options,
        new TenantContext());
}
