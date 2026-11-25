namespace Konnyaku.Academic.Contracts;

/// <summary>Size of the current tenant, for the platform console.</summary>
public interface IAcademicUsageApi
{
    /// <summary>Classes not yet completed or cancelled (planned, open, running).</summary>
    Task<int> CountActiveClassesAsync(CancellationToken cancellationToken);
}
