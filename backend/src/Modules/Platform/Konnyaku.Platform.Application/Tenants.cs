using Konnyaku.Platform.Contracts;
using Konnyaku.Platform.Domain;
using Konnyaku.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Konnyaku.Platform.Application;

public interface IPlatformDbContext
{
    DbSet<Tenant> Tenants { get; }
    DbSet<TenantDomain> TenantDomains { get; }
    DbSet<PlatformOperator> Operators { get; }
    DbSet<PlatformAuditEntry> AuditEntries { get; }

    void PublishFor(Guid tenantId, IIntegrationEvent integrationEvent, string? correlationId = null);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

/// <param name="BaseDomain">Tenants are served at <c>{slug}.{BaseDomain}</c>.</param>
/// <param name="AdminHost">The only host serving the platform console (<c>admin.{BaseDomain}</c> unless configured).</param>
public sealed record PlatformOptions(string BaseDomain, string AdminHost)
{
    public bool IsAdminHost(string? host) =>
        string.Equals(TenantDomain.NormalizeHost((host ?? string.Empty).Split(':')[0]), AdminHost, StringComparison.Ordinal);

    public string DefaultHost(string slug) => $"{slug}.{BaseDomain}";
}

/// <summary>Drops cached tenant lookups (by id and by host) after a status or domain change.</summary>
public interface ITenantDirectoryCache
{
    void Invalidate(Guid tenantId, IEnumerable<string> hosts);
}

public sealed record CreateTenantCommand(
    string Slug, string Name, string DefaultLocale, IReadOnlyList<string> EnabledLocales, string TimeZone,
    IReadOnlyList<string>? CustomHosts = null);

public sealed class CreateTenantHandler(IPlatformDbContext db, PlatformOptions options, AuditLog audit)
{
    public async Task<Result<TenantInfo>> HandleAsync(CreateTenantCommand command, PlatformActor actor, CancellationToken cancellationToken)
    {
        var created = Tenant.Create(command.Slug, command.Name, command.DefaultLocale, command.EnabledLocales ?? [], command.TimeZone);
        if (!created.IsSuccess)
        {
            return created.Error!;
        }

        var tenant = created.Value;
        if (await db.Tenants.AnyAsync(t => t.Slug == tenant.Slug, cancellationToken))
        {
            return Error.Conflict("platform.slug_taken", new Dictionary<string, object?> { ["slug"] = tenant.Slug });
        }

        var customHosts = (command.CustomHosts ?? []).Select(TenantDomain.NormalizeHost).ToList();
        foreach (var host in customHosts)
        {
            if (TenantDomains.CheckCustomHost(host, options) is { } error)
            {
                return error;
            }
        }

        var hosts = customHosts.Prepend(options.DefaultHost(tenant.Slug)).Distinct().ToList();
        if (await db.TenantDomains.AnyAsync(d => hosts.Contains(d.Host), cancellationToken))
        {
            return Error.Conflict("platform.host_taken");
        }

        db.Tenants.Add(tenant);
        db.TenantDomains.AddRange(hosts.Select(h => new TenantDomain(tenant.Id, h)));
        db.PublishFor(tenant.Id, new TenantCreatedV1(tenant.Id, tenant.Slug, tenant.DefaultLocale, tenant.TimeZone));
        audit.Record(actor, AuditActions.TenantCreate, tenant.Id, new Dictionary<string, string?>
        {
            ["slug"] = tenant.Slug,
            ["name"] = tenant.Name,
        });
        await db.SaveChangesAsync(cancellationToken);
        return tenant.ToInfo(hosts);
    }
}

internal static class TenantDomains
{
    /// <summary>A custom domain must be a valid host outside the base domain (sub-domains there are slugs or reserved).</summary>
    public static Error? CheckCustomHost(string host, PlatformOptions options)
    {
        if (!TenantDomain.IsValidHost(host))
        {
            return Error.Validation("platform.host_invalid");
        }

        return options.IsAdminHost(host) || host == options.BaseDomain || host.EndsWith($".{options.BaseDomain}", StringComparison.Ordinal)
            ? Error.Validation("platform.host_reserved", new Dictionary<string, object?> { ["baseDomain"] = options.BaseDomain })
            : null;
    }
}

public static class TenantMapping
{
    public static TenantInfo ToInfo(this Tenant tenant, IReadOnlyList<string> hosts) =>
        new(tenant.Id, tenant.Slug, tenant.Name, tenant.DefaultLocale, tenant.EnabledLocales, tenant.TimeZone, hosts, tenant.Status.ToString());
}
