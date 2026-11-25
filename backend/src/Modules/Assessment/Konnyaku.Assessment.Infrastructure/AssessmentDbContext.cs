using Ganss.Xss;
using Konnyaku.Application.Tenancy;
using Konnyaku.Assessment.Application;
using Konnyaku.Assessment.Domain;
using Konnyaku.Infrastructure.Persistence;
using Konnyaku.Infrastructure.Tenancy;
using Konnyaku.Platform.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Konnyaku.Assessment.Infrastructure;

public sealed class AssessmentDbContext(DbContextOptions<AssessmentDbContext> options, ITenantContext tenant)
    : ModuleDbContext(options, tenant), IAssessmentDbContext
{
    public const string SchemaName = "assessment";

    public override string Schema => SchemaName;

    public DbSet<QuestionBank> Banks => Set<QuestionBank>();
    public DbSet<QuestionGroup> Groups => Set<QuestionGroup>();
    public DbSet<Question> Questions => Set<Question>();
    public DbSet<Paper> Papers => Set<Paper>();
    public DbSet<Assignment> Assignments => Set<Assignment>();
    public DbSet<Attempt> Attempts => Set<Attempt>();
    public DbSet<AttemptItem> AttemptItems => Set<AttemptItem>();
    public DbSet<AttemptEvent> AttemptEvents => Set<AttemptEvent>();

    public void Reset() => ChangeTracker.Clear();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<QuestionBank>(b =>
        {
            b.ToTable("question_bank");
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(200);
            b.Property(x => x.LanguageCode).HasMaxLength(10);
            b.Property(x => x.Description).HasMaxLength(2000);
            b.Property(x => x.Visibility).HasConversion<string>().HasMaxLength(20);
            b.HasIndex(x => new { x.TenantId, x.LanguageCode, x.Name });
        });
        modelBuilder.Entity<QuestionGroup>(b =>
        {
            b.ToTable("question_group");
            b.HasKey(x => x.Id);
            b.Property(x => x.Title).HasMaxLength(200);
            b.HasIndex(x => new { x.TenantId, x.BankId });
        });
        modelBuilder.Entity<Question>(b =>
        {
            b.ToTable("question");
            b.HasKey(x => x.Id);
            b.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Content).HasJsonConversion();
            b.Property(x => x.Key).HasColumnName("answer_key").HasJsonConversion();
            b.Property(x => x.Points).HasPrecision(8, 2);
            b.Property(x => x.FrameworkCode).HasMaxLength(20);
            b.Property(x => x.SkillCode).HasMaxLength(30);
            b.Property(x => x.LevelCode).HasMaxLength(20);
            b.HasIndex(x => new { x.TenantId, x.BankId, x.Archived });
            b.HasIndex(x => x.GroupId);
            b.HasIndex(x => x.Tags).HasMethod("gin");
        });
        modelBuilder.Entity<Paper>(b =>
        {
            b.ToTable("assessment");
            b.HasKey(x => x.Id);
            b.Property(x => x.Title).HasMaxLength(200);
            b.Property(x => x.LanguageCode).HasMaxLength(10);
            b.Property(x => x.FrameworkCode).HasMaxLength(20);
            b.Property(x => x.Description).HasMaxLength(2000);
            b.Property(x => x.Layout).HasJsonConversion();
            b.HasIndex(x => new { x.TenantId, x.UpdatedAt });
        });
        modelBuilder.Entity<Assignment>(b =>
        {
            b.ToTable("assignment");
            b.HasKey(x => x.Id);
            b.Property(x => x.Title).HasMaxLength(200);
            b.Property(x => x.Target).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.LatePolicy).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.ScoringRule).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Grader).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.ScoreRelease).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.AnswerVisibility).HasConversion<string>().HasMaxLength(20);
            b.HasIndex(x => new { x.TenantId, x.ClassId, x.OpensAt });
            b.HasIndex(x => new { x.TenantId, x.BranchId, x.OpensAt });
        });
        modelBuilder.Entity<Attempt>(b =>
        {
            b.ToTable("attempt");
            b.HasKey(x => x.Id);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Layout).HasJsonConversion();
            b.Property(x => x.MaxScore).HasPrecision(10, 2);
            b.Property(x => x.RawScore).HasPrecision(10, 2);
            b.Property(x => x.Score).HasPrecision(10, 2);
            b.Property(x => x.RowVersion).IsRowVersion();
            b.Ignore(x => x.Timed);

            // One open attempt per learner and assignment, enforced by the database.
            b.HasIndex(x => new { x.AssignmentId, x.StudentId }).IsUnique().HasFilter("status = 'InProgress'").HasDatabaseName("ux_attempt_open");
            b.HasIndex(x => new { x.AssignmentId, x.StudentId, x.Number }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.UserId });
            b.HasIndex(x => new { x.Status, x.DeadlineAt });
        });
        modelBuilder.Entity<AttemptItem>(b =>
        {
            b.ToTable("attempt_item");
            b.HasKey(x => x.Id);
            b.Property(x => x.Snapshot).HasJsonConversion();
            b.Property(x => x.Response).HasJsonConversion();
            b.Property(x => x.Bands).HasJsonConversion();
            b.Property(x => x.Score).HasPrecision(8, 2);
            b.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
            b.Ignore(x => x.NeedsTeacher);
            b.HasIndex(x => new { x.AttemptId, x.Ordinal }).IsUnique();
        });
        modelBuilder.Entity<AttemptEvent>(b =>
        {
            b.ToTable("attempt_event");
            b.HasKey(x => x.Id);
            b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            b.HasIndex(x => new { x.AttemptId, x.OccurredAt });
        });
    }
}

/// <summary>Allow-listed rich text: basic formatting, lists, ruby (furigana, pinyin). No attributes, links, images or styles.</summary>
public sealed partial class RichTextSanitizer : IRichText
{
    private const int MaxLength = 50_000;

    private static readonly HtmlSanitizer Sanitizer = Create();

    public string Sanitize(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var input = html.Length > MaxLength ? html[..MaxLength] : html;

        // Disallowed tags keep their text (pasted <div>, <font>), except scripts and styles, which are dropped whole.
        input = ScriptOrStyle().Replace(input, string.Empty);
        return Sanitizer.Sanitize(input).Trim();
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1\s*>", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline)]
    private static partial System.Text.RegularExpressions.Regex ScriptOrStyle();

    private static HtmlSanitizer Create()
    {
        var options = new HtmlSanitizerOptions
        {
            AllowedTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "p", "br", "b", "strong", "i", "em", "u", "s", "sub", "sup", "mark", "small", "span", "ruby", "rt", "rp", "ul", "ol", "li",
                "blockquote", "code", "pre", "h3", "h4", "table", "thead", "tbody", "tr", "th", "td",
            },
            AllowedAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            AllowedCssProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            AllowedSchemes = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            UriAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        };
        var sanitizer = new HtmlSanitizer(options);
        sanitizer.KeepChildNodes = true;
        return sanitizer;
    }
}

public static class AssessmentInfrastructure
{
    public static IServiceCollection AddAssessmentInfrastructure(this IServiceCollection services, IConfiguration configuration, string connectionName)
    {
        services.AddModuleDbContext<AssessmentDbContext>(configuration, connectionName, AssessmentDbContext.SchemaName);
        services.AddScoped<IAssessmentDbContext>(sp => sp.GetRequiredService<AssessmentDbContext>());
        services.AddSingleton<IRichText, RichTextSanitizer>();
        return services;
    }

    public static IServiceCollection AddAssessmentWorker(this IServiceCollection services)
    {
        services.AddSingleton<AttemptDeadlineService>();
        services.AddHostedService(sp => sp.GetRequiredService<AttemptDeadlineService>());
        return services;
    }
}

/// <summary>
/// Worker job: every 30 seconds, tenant by tenant (own scope and tenant context, so RLS applies), hands in the attempts
/// whose deadline passed — the learner may have closed the browser. Attempts are also handed in when touched after
/// their deadline, so the delay here never changes a result (the submission time is the deadline).
/// </summary>
public sealed class AttemptDeadlineService(IServiceScopeFactory scopeFactory, ILogger<AttemptDeadlineService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Attempt deadline run failed");
            }
        }
    }

    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid> tenants;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            tenants = await scope.ServiceProvider.GetRequiredService<IPlatformApi>().ListTenantIdsAsync(cancellationToken);
        }

        var total = 0;
        foreach (var tenantId in tenants)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().Set(tenantId);
            var count = await scope.ServiceProvider.GetRequiredService<ExpiredAttempts>().RunAsync(cancellationToken);
            if (count > 0)
            {
                logger.LogInformation("Handed in {Count} expired attempts of tenant {TenantId}", count, tenantId);
            }

            total += count;
        }

        return total;
    }
}

internal sealed class AssessmentDesignTimeFactory : IDesignTimeDbContextFactory<AssessmentDbContext>
{
    public AssessmentDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<AssessmentDbContext>()
            .UseNpgsql("Host=localhost;Database=design_time", o => o.MigrationsHistoryTable("__ef_migrations_history", AssessmentDbContext.SchemaName))
            .UseSnakeCaseNamingConvention()
            .Options,
        new TenantContext());
}
