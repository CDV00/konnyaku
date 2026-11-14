using Konnyaku.SharedKernel;

namespace Konnyaku.Organization.Domain;

/// <summary>
/// A day without classes. Holidays are data, not rules: lunar new year moves every year.
/// <see cref="BranchId"/> null = the whole center.
/// </summary>
public sealed class Holiday : Entity, ITenantOwned
{
    private Holiday()
    {
    }

    public Holiday(DateOnly date, LocalizedString name, Guid? branchId)
    {
        Date = date;
        Name = name;
        BranchId = branchId;
    }

    public Guid TenantId { get; private set; }
    public DateOnly Date { get; private set; }
    public LocalizedString Name { get; private set; } = null!;
    public Guid? BranchId { get; private set; }
}

/// <summary>A physical room of a branch.</summary>
public sealed class Room : Entity, ITenantOwned
{
    private Room()
    {
    }

    public Room(Guid branchId, string code, int capacity)
    {
        BranchId = branchId;
        Code = code.Trim().ToUpperInvariant();
        Capacity = capacity;
    }

    public Guid TenantId { get; private set; }
    public Guid BranchId { get; private set; }
    public string Code { get; private set; } = string.Empty;
    public int Capacity { get; private set; }
}
