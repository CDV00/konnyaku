using Konnyaku.Application.Tenancy;

namespace Konnyaku.Infrastructure.Tenancy;

/// <summary>Scoped tenant holder, set once per request/job/event by the entry point.</summary>
public sealed class TenantContext : ITenantContext
{
    public Guid? TenantId { get; private set; }

    public void Set(Guid tenantId)
    {
        if (TenantId is { } current && current != tenantId)
        {
            throw new InvalidOperationException("The tenant of a scope cannot change once set.");
        }

        TenantId = tenantId;
    }
}
