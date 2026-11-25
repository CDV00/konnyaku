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
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Konnyaku.Infrastructure.Redis;

namespace Konnyaku.Notification.Infrastructure;

public sealed class NotificationDbContext(DbContextOptions<NotificationDbContext> options, ITenantContext tenant)
    : ModuleDbContext(options, tenant), INotificationDbContext
{
    public const string SchemaName = "notification";

    public override string Schema => SchemaName;

    public DbSet<NotificationMessage> Messages => Set<NotificationMessage>();
    public DbSet<DispatchItem> DispatchItems => Set<DispatchItem>();
    public DbSet<InboxItem> InboxItems => Set<InboxItem>();
    public DbSet<NotificationPreference> Preferences => Set<NotificationPreference>();
    public DbSet<PushSubscription> PushSubscriptions => Set<PushSubscription>();

    /// <summary>One sequence for every inbox item (all tenants): the sync cursor of the web app.</summary>
    public const string InboxSequence = "inbox_seq";

    public async Task<long> RenumberInboxItemAsync(Guid itemId, CancellationToken cancellationToken)
    {
        // Through EF so the tenant RLS of the connection applies; the new number comes back with RETURNING.
        var numbers = await Database.SqlQuery<long>(
                $"UPDATE notification.inbox_item SET seq = nextval('notification.inbox_seq') WHERE id = {itemId} RETURNING seq AS \"Value\"")
            .ToListAsync(cancellationToken);
        return numbers.FirstOrDefault();
    }

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
        modelBuilder.HasSequence<long>(InboxSequence);
        modelBuilder.Entity<InboxItem>(b =>
        {
            b.ToTable("inbox_item");
            b.HasKey(x => x.Id);
            b.Property(x => x.Kind).HasMaxLength(60);
            b.Property(x => x.TitleKey).HasMaxLength(100);
            b.Property(x => x.Params).HasJsonConversion();
            b.Property(x => x.Link).HasMaxLength(300);
            b.Property(x => x.DeduplicationKey).HasMaxLength(InboxItem.MaxKeyLength);
            b.Property(x => x.Seq).HasDefaultValueSql($"nextval('{SchemaName}.{InboxSequence}')");
            b.HasIndex(x => new { x.TenantId, x.DeduplicationKey }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.UserId, x.Seq });
            b.HasIndex(x => new { x.TenantId, x.UserId, x.ReadAt });
        });
        modelBuilder.Entity<NotificationPreference>(b =>
        {
            b.ToTable("notification_preference");
            b.HasKey(x => x.Id);
            b.Property(x => x.Kind).HasMaxLength(60);
            b.HasIndex(x => new { x.TenantId, x.UserId, x.Kind }).IsUnique();
        });
        modelBuilder.Entity<PushSubscription>(b =>
        {
            b.ToTable("push_subscription");
            b.HasKey(x => x.Id);
            b.Property(x => x.Endpoint).HasMaxLength(PushSubscription.MaxEndpointLength);
            b.Property(x => x.P256dh).HasMaxLength(200);
            b.Property(x => x.Auth).HasMaxLength(100);
            b.HasIndex(x => new { x.TenantId, x.Endpoint }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.UserId });
        });
        modelBuilder.Entity<NotificationMessage>(b => b.Property(x => x.DigestKey).HasMaxLength(200));
        modelBuilder.Entity<NotificationMessage>(b => b.HasIndex(x => new { x.TenantId, x.DigestKey, x.Status }));
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

        // Inbox, channel router, Web Push, realtime signals (doc 10.9).
        services.AddScoped<NotificationPreferences>();
        services.AddScoped<NotificationRouter>();
        services.AddScoped<InboxHandlers>();
        services.AddSingleton(sp => sp.GetRequiredService<IConfiguration>().GetSection("Notification:Push").Get<PushOptions>() ?? new PushOptions());
        services.AddSingleton<VapidPushSender>(sp => new VapidPushSender(sp.GetRequiredService<PushOptions>(), sp.GetRequiredService<ILogger<VapidPushSender>>()));
        services.TryAddSingleton<IPushSender>(sp => sp.GetRequiredService<VapidPushSender>());
        services.AddSingleton(sp => sp.GetRequiredService<IConfiguration>().GetSection("Realtime").Get<RealtimeOptions>() ?? new RealtimeOptions());
        services.AddSingleton(sp => new RealtimeTickets(sp.GetRequiredService<RealtimeOptions>(),
            sp.GetRequiredService<IConfiguration>()["Rtc:SignalingSecret"], sp.GetRequiredService<TimeProvider>()));
        if (KonnyakuRedis.IsConfigured(configuration))
        {
            services.AddKonnyakuRedis(configuration);
            services.TryAddSingleton<IRealtimePublisher, RedisRealtimePublisher>();
        }
        else
        {
            services.TryAddSingleton<IRealtimePublisher, NullRealtimePublisher>();
        }

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
