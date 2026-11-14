using Konnyaku.Platform.Contracts;
using Konnyaku.Platform.Domain;
using Konnyaku.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Konnyaku.Platform.Application;

public interface IPlatformDbContext
{
    DbSet<Tenant> Tenants { get; }
    DbSet<TenantDomain> TenantDomains { get; }

    void PublishFor(Guid tenantId, IIntegrationEvent integrationEvent, string? correlationId = null);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

public sealed record PlatformOptions(string BaseDomain);

public sealed record CreateTenantCommand(
    string Slug, string Name, string DefaultLocale, IReadOnlyList<string> EnabledLocales, string TimeZone,
    IReadOnlyList<string>? CustomHosts = null);

public sealed class CreateTenantHandler(IPlatformDbContext db, PlatformOptions options)
{
    public async Task<Result<TenantInfo>> HandleAsync(CreateTenantCommand command, CancellationToken cancellationToken)
    {
        var created = Tenant.Create(command.Slug, command.Name, command.DefaultLocale, command.EnabledLocales, command.TimeZone);
        if (!created.IsSuccess)
        {
            return created.Error!;
        }

        var tenant = created.Value;
        if (await db.Tenants.AnyAsync(t => t.Slug == tenant.Slug, cancellationToken))
        {
            return Error.Conflict("platform.slug_taken", new Dictionary<string, object?> { ["slug"] = tenant.Slug });
        }

        var hosts = new[] { $"{tenant.Slug}.{options.BaseDomain}" }
            .Concat(command.CustomHosts ?? [])
            .Select(h => h.Trim().ToLowerInvariant())
            .Distinct()
            .ToList();
        if (await db.TenantDomains.AnyAsync(d => hosts.Contains(d.Host), cancellationToken))
        {
            return Error.Conflict("platform.host_taken");
        }

        db.Tenants.Add(tenant);
        db.TenantDomains.AddRange(hosts.Select(h => new TenantDomain(tenant.Id, h)));
        db.PublishFor(tenant.Id, new TenantCreatedV1(tenant.Id, tenant.Slug, tenant.DefaultLocale, tenant.TimeZone));
        await db.SaveChangesAsync(cancellationToken);
        return tenant.ToInfo(hosts);
    }
}

public static class TenantMapping
{
    public static TenantInfo ToInfo(this Tenant tenant, IReadOnlyList<string> hosts) =>
        new(tenant.Id, tenant.Slug, tenant.Name, tenant.DefaultLocale, tenant.EnabledLocales, tenant.TimeZone, hosts);
}
