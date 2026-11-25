using Konnyaku.Academic.Application;
using Konnyaku.Academic.Contracts;
using Konnyaku.Academic.Domain;
using Konnyaku.Application.Tenancy;
using Konnyaku.Infrastructure.Auditing;
using Konnyaku.Infrastructure.Persistence;
using Konnyaku.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Konnyaku.Academic.Infrastructure;

public sealed class AcademicDbContext(DbContextOptions<AcademicDbContext> options, ITenantContext tenant)
    : ModuleDbContext(options, tenant), IAcademicDbContext
{
    public const string SchemaName = "academic";

    public override string Schema => SchemaName;

    public DbSet<StudyProgram> Programs => Set<StudyProgram>();
    public DbSet<CourseLevel> CourseLevels => Set<CourseLevel>();
    public DbSet<CurriculumLesson> Lessons => Set<CurriculumLesson>();
    public DbSet<Class> Classes => Set<Class>();
    public DbSet<Enrollment> Enrollments => Set<Enrollment>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        var localized = new LocalizedStringConverter();
        modelBuilder.Entity<StudyProgram>(b =>
        {
            b.ToTable("program");
            b.HasKey(x => x.Id);
            b.Property(x => x.Code).HasMaxLength(30);
            b.Property(x => x.Name).HasConversion(localized, LocalizedStringConverter.Comparer).HasColumnType("jsonb");
            b.Property(x => x.LanguageCode).HasMaxLength(20);
            b.Property(x => x.FrameworkCode).HasMaxLength(30);
            b.Property(x => x.AgeGroup).HasMaxLength(40);
            b.Property(x => x.Goal).HasMaxLength(200);
            b.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
            b.HasMany(x => x.Levels).WithOne().HasForeignKey(x => x.ProgramId);
        });
        modelBuilder.Entity<CourseLevel>(b =>
        {
            b.ToTable("course_level");
            b.HasKey(x => x.Id);
            b.Property(x => x.Code).HasMaxLength(30);
            b.Property(x => x.Name).HasConversion(localized, LocalizedStringConverter.Comparer).HasColumnType("jsonb");
            b.Property(x => x.TargetLevelCode).HasMaxLength(20);
            b.HasIndex(x => new { x.TenantId, x.ProgramId, x.Code }).IsUnique();
            b.HasMany(x => x.Lessons).WithOne().HasForeignKey(x => x.CourseLevelId);
        });
        modelBuilder.Entity<CurriculumLesson>(b =>
        {
            b.ToTable("curriculum_lesson");
            b.HasKey(x => x.Id);
            b.Property(x => x.Title).HasConversion(localized, LocalizedStringConverter.Comparer).HasColumnType("jsonb");
            b.HasIndex(x => new { x.TenantId, x.CourseLevelId, x.Ordinal }).IsUnique();
        });
        modelBuilder.Entity<Class>(b =>
        {
            b.ToTable("class");
            b.HasKey(x => x.Id);
            b.Property(x => x.Code).HasMaxLength(30);
            b.Property(x => x.TimeZone).HasMaxLength(64);
            b.Property(x => x.DeliveryMode).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.BookingMode).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.BranchId, x.StartsOn });
            b.ToTable(t => t.HasCheckConstraint("ck_class_enrolled_within_capacity", "enrolled_count >= 0 AND enrolled_count <= capacity"));
            b.Ignore(x => x.IsClosed);
        });
        modelBuilder.Entity<Enrollment>(b =>
        {
            b.ToTable("enrollment");
            b.HasKey(x => x.Id);
            b.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.PreferredMode).HasMaxLength(20);
            b.Ignore(x => x.HoldsSeat);
            b.HasIndex(x => new { x.TenantId, x.ClassId });
            b.HasIndex(x => new { x.TenantId, x.StudentId });
            b.HasIndex(x => new { x.ClassId, x.StudentId }).IsUnique().HasFilter("status IN ('Active', 'Frozen')");
        });
        modelBuilder.AddChangeLog();
    }
}

public static class AcademicInfrastructure
{
    public static IServiceCollection AddAcademicInfrastructure(this IServiceCollection services, IConfiguration configuration, string connectionName)
    {
        services.AddModuleDbContext<AcademicDbContext>(configuration, connectionName, AcademicDbContext.SchemaName);
        services.AddScoped<IAcademicDbContext>(sp => sp.GetRequiredService<AcademicDbContext>());
        services.AddScoped<AcademicQueries>();
        services.AddScoped<IAcademicApi>(sp => sp.GetRequiredService<AcademicQueries>());
        services.AddScoped<IAcademicUsageApi, AcademicUsageQueries>();
        services.AddSingleton(TimeProvider.System);
        return services;
    }
}

internal sealed class AcademicDesignTimeFactory : IDesignTimeDbContextFactory<AcademicDbContext>
{
    public AcademicDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<AcademicDbContext>()
            .UseNpgsql("Host=localhost;Database=design_time", o => o.MigrationsHistoryTable("__ef_migrations_history", AcademicDbContext.SchemaName))
            .UseSnakeCaseNamingConvention()
            .Options,
        new TenantContext());
}
