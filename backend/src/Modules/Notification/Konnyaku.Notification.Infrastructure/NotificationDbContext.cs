using Konnyaku.Application.Tenancy;
using Konnyaku.Infrastructure.Persistence;
using Konnyaku.Infrastructure.Tenancy;
using Konnyaku.Notification.Application;
using Konnyaku.Notification.Contracts;
using Konnyaku.Notification.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Konnyaku.Notification.Infrastructure;

public sealed class NotificationDbContext(DbContextOptions<NotificationDbContext> options, ITenantContext tenant)
    : ModuleDbContext(options, tenant), INotificationDbContext
{
    public const string SchemaName = "notification";

    public override string Schema => SchemaName;

    public DbSet<NotificationMessage> Messages => Set<NotificationMessage>();
    public DbSet<DispatchItem> DispatchItems => Set<DispatchItem>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NotificationMessage>(b =>
        {
            b.ToTable("notification_message");
            b.HasKey(x => x.Id);
            b.Property(x => x.Channel).HasMaxLength(20);
            b.Property(x => x.TemplateKey).HasMaxLength(100);
            b.Property(x => x.RecipientAddress).HasMaxLength(320);
            b.Property(x => x.RecipientName).HasMaxLength(200);
            b.Property(x => x.Locale).HasMaxLength(10);
            b.Property(x => x.Data).HasJsonConversion();
            b.Property(x => x.DeduplicationKey).HasMaxLength(300);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.LastError).HasMaxLength(NotificationMessage.MaxErrorLength);
            b.Property(x => x.Subject).HasMaxLength(300);
            b.HasIndex(x => new { x.TenantId, x.DeduplicationKey }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.CreatedAt });
            b.HasIndex(x => new { x.TenantId, x.Status, x.CreatedAt });
        });
        modelBuilder.Entity<DispatchItem>(b =>
        {
            b.ToTable("dispatch_item");
            b.HasKey(x => x.Id);
            b.Property(x => x.Kind).HasMaxLength(40);
            b.HasIndex(x => new { x.Kind, x.RefId }).IsUnique();
            b.HasIndex(x => x.DueAt);
        });
    }
}

public sealed class SmtpOptions
{
    /// <summary>Empty: email is not configured; messages wait (transient error) until it is.</summary>
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public string? Username { get; set; }
    public string? Password { get; set; }

    /// <summary>Auto (StartTLS when offered), StartTls, SslOnConnect or None (dev Mailpit).</summary>
    public string Security { get; set; } = "Auto";
    public string FromAddress { get; set; } = string.Empty;

    /// <summary>Sender name when the tenant has none; normally the center name is used.</summary>
    public string FromName { get; set; } = "Konnyaku";
    public int TimeoutSeconds { get; set; } = 30;
}

public static class NotificationInfrastructure
{
    public static IServiceCollection AddNotificationInfrastructure(this IServiceCollection services, IConfiguration configuration, string connectionName)
    {
        services.AddModuleDbContext<NotificationDbContext>(configuration, connectionName, NotificationDbContext.SchemaName);
        services.AddScoped<INotificationDbContext>(sp => sp.GetRequiredService<NotificationDbContext>());

        // Bound when first used, so host and test overrides of the configuration always apply.
        services.AddSingleton(sp => sp.GetRequiredService<IConfiguration>().GetSection("Notification").Get<NotificationOptions>() ?? new NotificationOptions());
        services.AddSingleton(sp => sp.GetRequiredService<IConfiguration>().GetSection("Notification:Smtp").Get<SmtpOptions>() ?? new SmtpOptions());
        services.AddSingleton<ITemplateRenderer, EmbeddedTemplateRenderer>();
        services.AddSingleton<IEmailSender, SmtpEmailSender>();
        services.AddScoped<INotificationChannel, EmailChannel>();

        services.AddScoped<Recipients>();
        services.AddScoped<TenantBranding>();
        services.AddScoped<NotificationQueue>();
        services.AddScoped<INotificationApi>(sp => sp.GetRequiredService<NotificationQueue>());
        services.AddScoped<DispatchSchedule>();
        services.AddScoped<NotificationLog>();
        services.AddSingleton<NotificationDispatcher>();
        return services;
    }
}

internal sealed class NotificationDesignTimeFactory : IDesignTimeDbContextFactory<NotificationDbContext>
{
    public NotificationDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<NotificationDbContext>()
            .UseNpgsql("Host=localhost;Database=design_time", o => o.MigrationsHistoryTable("__ef_migrations_history", NotificationDbContext.SchemaName))
            .UseSnakeCaseNamingConvention()
            .Options,
        new TenantContext());
}
