using Konnyaku.SharedKernel;

namespace Konnyaku.Organization.Contracts;

public sealed record BranchDto(Guid Id, string Code, IReadOnlyDictionary<string, string> Name, string TimeZone, bool IsVirtual);

/// <summary>Read-only API other modules may call synchronously. State changes travel as events.</summary>
public interface IOrganizationApi
{
    Task<BranchDto?> GetBranchAsync(Guid branchId, CancellationToken cancellationToken);
}

public sealed record BranchCreatedV1(Guid BranchId, string Code, string TimeZone, bool IsVirtual) : IIntegrationEvent;
