using Konnyaku.Application.Authorization;
using Konnyaku.Application.Tenancy;
using Konnyaku.Identity.Application;
using Konnyaku.Identity.Contracts;
using Konnyaku.Identity.Domain;
using Konnyaku.Infrastructure.Auditing;
using Konnyaku.Infrastructure.Persistence;
using Konnyaku.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Konnyaku.Identity.Infrastructure;

/// <summary>
/// Users and staff passwords are platform-wide (one person, many centers). Center-issued credentials, memberships
/// and roles are tenant-owned and protected by RLS.
/// </summary>
public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options, ITenantContext tenant)
    : ModuleDbContext(options, tenant), IIdentityDbContext
{
    public const string SchemaName = "identity";

    public override string Schema => SchemaName;

    public DbSet<User> Users => Set<User>();
    public DbSet<PasswordCredential> PasswordCredentials => Set<PasswordCredential>();
    public DbSet<LocalCredential> LocalCredentials => Set<LocalCredential>();
    public DbSet<Membership> Memberships => Set<Membership>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();
    public DbSet<SecurityEvent> SecurityEvents => Set<SecurityEvent>();

    /// <summary>
    /// Counted by <c>identity.member_tenant_count</c>, a SECURITY DEFINER function (memberships of other centers are
    /// hidden by RLS). It answers NULL when its owner cannot bypass RLS, and NULL counts as "shared": fail closed.
    /// </summary>
    public async Task<int?> CountCentersOfUserAsync(Guid userId, CancellationToken cancellationToken) =>
        await Database.SqlQuery<int?>($"SELECT identity.member_tenant_count({userId}) AS \"Value\"").FirstAsync(cancellationToken);

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(b =>
        {
            b.ToTable("user");
            b.HasKey(x => x.Id);
            b.Property(x => x.Email).HasMaxLength(320);
            b.HasIndex(x => x.Email).IsUnique().HasFilter("email IS NOT NULL");
            b.Property(x => x.DisplayName).HasMaxLength(200);
            b.Property(x => x.PreferredLocale).HasMaxLength(20);
        });
        modelBuilder.Entity<PasswordCredential>(b =>
        {
            b.ToTable("password_credential");
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.UserId).IsUnique();
            b.Property(x => x.TotpSecret).HasMaxLength(200);
            b.Property(x => x.PendingTotpSecret).HasMaxLength(200);
            b.Ignore(x => x.TwoFactorEnabled);
        });
        modelBuilder.Entity<LocalCredential>(b =>
        {
            b.ToTable("local_credential");
            b.HasKey(x => x.Id);
            b.Property(x => x.Username).HasMaxLength(64);
            b.HasIndex(x => new { x.TenantId, x.Username }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.UserId }).IsUnique();
        });
        modelBuilder.Entity<Membership>(b =>
        {
            b.ToTable("membership");
            b.HasKey(x => x.Id);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.HasIndex(x => new { x.TenantId, x.UserId }).IsUnique();
            b.HasMany(x => x.Roles).WithOne().HasForeignKey(x => x.MembershipId);
        });
        modelBuilder.Entity<RoleAssignment>(b =>
        {
            b.ToTable("role_assignment");
            b.HasKey(x => x.Id);
            b.Property(x => x.Role).HasMaxLength(40);
            b.Property(x => x.ScopeType).HasConversion<string>().HasMaxLength(20);
            b.HasIndex(x => new { x.TenantId, x.MembershipId });
        });
        modelBuilder.Entity<UserSession>(b =>
        {
            b.ToTable("user_session");
            b.HasKey(x => x.Id);
            b.Property(x => x.Audience).HasMaxLength(10);
            b.Property(x => x.RevokedReason).HasMaxLength(40);
            b.Property(x => x.UserAgent).HasMaxLength(300);
            b.Property(x => x.IpAddress).HasMaxLength(64);
            b.HasIndex(x => new { x.TenantId, x.UserId });
        });
        modelBuilder.Entity<PasswordResetToken>(b =>
        {
            b.ToTable("password_reset_token");
            b.HasKey(x => x.Id);
            b.Property(x => x.TokenHash).HasMaxLength(64);
            b.Property(x => x.RequestedFromIp).HasMaxLength(64);
            b.HasIndex(x => x.TokenHash).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.UserId });
        });
        modelBuilder.Entity<SecurityEvent>(b =>
        {
            b.ToTable("security_event");
            b.HasKey(x => x.Id);
            b.Property(x => x.Kind).HasMaxLength(40);
            b.Property(x => x.Subject).HasMaxLength(320);
            b.Property(x => x.ActorName).HasMaxLength(200);
            b.Property(x => x.IpAddress).HasMaxLength(64);
            b.Property(x => x.UserAgent).HasMaxLength(300);
            b.Property(x => x.Detail).HasMaxLength(200);
            b.HasIndex(x => new { x.TenantId, x.OccurredAt });
            b.HasIndex(x => new { x.TenantId, x.UserId });
        });
        modelBuilder.AddChangeLog();
    }
}

public static class IdentityInfrastructure
{
    public static IServiceCollection AddIdentityInfrastructure(this IServiceCollection services, IConfiguration configuration, string connectionName)
    {
        services.AddModuleDbContext<IdentityDbContext>(configuration, connectionName, IdentityDbContext.SchemaName);
        services.AddScoped<IIdentityDbContext>(sp => sp.GetRequiredService<IdentityDbContext>());
        services.AddScoped<IIdentityApi, IdentityQueries>();
        services.AddHttpContextAccessor();
        services.AddScoped<IAccessControl, AccessControl>();
        services.AddMemoryCache();
        // Every host that builds the Identity handlers (the Worker validates them at start-up too) needs the protector.
        services.TryAddSingleton<Konnyaku.Application.Security.ISecretProtector, Konnyaku.Infrastructure.Security.AesSecretProtector>();
        services.AddSingleton<SessionCache>();
        services.AddSingleton<ISessionCache>(sp => sp.GetRequiredService<SessionCache>());
        services.AddScoped<ISessionValidator, SessionValidator>();
        services.AddScoped<IRequestContext, HttpRequestContext>();
        services.AddSingleton(_ => TwoFactorConfiguration.Policy(configuration));
        services.AddSingleton<IPasswordResetMailer, LoggingPasswordResetMailer>();
        services.AddScoped<SecurityLog>();
        services.AddScoped<SessionManager>();
        services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
        services.AddSingleton(TimeProvider.System);
        return services;
    }
}

internal sealed class IdentityDesignTimeFactory : IDesignTimeDbContextFactory<IdentityDbContext>
{
    public IdentityDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql("Host=localhost;Database=design_time", o => o.MigrationsHistoryTable("__ef_migrations_history", IdentityDbContext.SchemaName))
            .UseSnakeCaseNamingConvention()
            .Options,
        new TenantContext());
}
