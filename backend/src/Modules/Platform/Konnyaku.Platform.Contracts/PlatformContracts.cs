using Konnyaku.SharedKernel;

namespace Konnyaku.Platform.Contracts;

public sealed record TenantInfo(
    Guid Id, string Slug, string Name, string DefaultLocale, IReadOnlyList<string> EnabledLocales, string TimeZone,
    IReadOnlyList<string> Hosts);

public interface IPlatformApi
{
    /// <summary>Tenant owning a host name (subdomain or custom domain); cached.</summary>
    Task<TenantInfo?> ResolveHostAsync(string host, CancellationToken cancellationToken);

    Task<TenantInfo?> GetTenantAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>Every tenant id, for background jobs that run tenant by tenant (retention, reminders).</summary>
    Task<IReadOnlyList<Guid>> ListTenantIdsAsync(CancellationToken cancellationToken);
}

public sealed record TenantCreatedV1(Guid TenantId, string Slug, string DefaultLocale, string TimeZone) : IIntegrationEvent;
