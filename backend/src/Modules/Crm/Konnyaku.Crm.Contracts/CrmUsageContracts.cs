namespace Konnyaku.Crm.Contracts;

/// <summary>Size of the current tenant, for the platform console.</summary>
public interface ICrmUsageApi
{
    Task<int> CountStudentsAsync(CancellationToken cancellationToken);
}
