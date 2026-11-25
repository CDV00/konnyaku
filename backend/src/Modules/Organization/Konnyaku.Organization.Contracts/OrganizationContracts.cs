using Konnyaku.SharedKernel;

namespace Konnyaku.Organization.Contracts;

public sealed record BranchDto(Guid Id, string Code, IReadOnlyDictionary<string, string> Name, string TimeZone, bool IsVirtual, bool IsActive);

/// <summary>Read-only API other modules may call synchronously. State changes travel as events.</summary>
public interface IOrganizationApi
{
    Task<BranchDto?> GetBranchAsync(Guid branchId, CancellationToken cancellationToken);

    Task<RoomDto?> GetRoomAsync(Guid roomId, CancellationToken cancellationToken);

    /// <summary>Days off for a branch in [from, to]: center-wide holidays plus those of the branch.</summary>
    Task<IReadOnlySet<DateOnly>> ListHolidaysAsync(Guid branchId, DateOnly from, DateOnly to, CancellationToken cancellationToken);
}

public sealed record RoomDto(Guid Id, Guid BranchId, string Code, int Capacity, bool IsActive);

public sealed record HolidayDto(Guid Id, DateOnly Date, IReadOnlyDictionary<string, string> Name, Guid? BranchId);

public sealed record BranchCreatedV1(Guid BranchId, string Code, string TimeZone, bool IsVirtual) : IIntegrationEvent;
