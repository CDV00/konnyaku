using Konnyaku.SharedKernel;

namespace Konnyaku.Platform.Contracts;

/// <param name="Status"><c>Active</c> or <c>Suspended</c> (sign-in and API refused, public site shows a notice).</param>
public sealed record TenantInfo(
    Guid Id, string Slug, string Name, string DefaultLocale, IReadOnlyList<string> EnabledLocales, string TimeZone,
    IReadOnlyList<string> Hosts, string Status);

public static class TenantStatuses
{
    public const string Active = "Active";
    public const string Suspended = "Suspended";
}

public interface IPlatformApi
{
    /// <summary>Tenant owning a host name (subdomain or custom domain); cached.</summary>
    Task<TenantInfo?> ResolveHostAsync(string host, CancellationToken cancellationToken);

    Task<TenantInfo?> GetTenantAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>Every tenant id, for background jobs that run tenant by tenant (retention, reminders).</summary>
    Task<IReadOnlyList<Guid>> ListTenantIdsAsync(CancellationToken cancellationToken);
}

public sealed record TenantCreatedV1(Guid TenantId, string Slug, string DefaultLocale, string TimeZone) : IIntegrationEvent;
