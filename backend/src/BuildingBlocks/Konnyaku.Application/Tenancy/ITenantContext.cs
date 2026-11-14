namespace Konnyaku.Application.Tenancy;

/// <summary>The tenant of the current request, job or event. There is no default tenant.</summary>
public interface ITenantContext
{
    Guid? TenantId { get; }

    Guid RequiredTenantId => TenantId ?? throw new TenantContextMissingException();
}

public sealed class TenantContextMissingException()
    : InvalidOperationException("No tenant context is set for this operation.");
