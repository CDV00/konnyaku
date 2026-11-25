using Konnyaku.SharedKernel;

namespace Konnyaku.Organization.Domain;

/// <summary>
/// A day without classes. Holidays are data, not rules: lunar new year moves every year.
/// <see cref="BranchId"/> null = the whole center. Nothing points to a holiday (sessions are planned around it), so a
/// mistaken one may be deleted; the change log keeps the trace.
/// </summary>
public sealed class Holiday : Entity, ITenantOwned, IAuditable
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

    Guid IAuditable.AuditSubjectId => Id;

    public void Update(DateOnly date, LocalizedString name, Guid? branchId)
    {
        Date = date;
        Name = name;
        BranchId = branchId;
    }
}

/// <summary>A physical room of a branch. Rooms never move to another branch; an unused room is deactivated.</summary>
public sealed class Room : Entity, ITenantOwned, IAuditable
{
    private Room()
    {
    }

    public Room(Guid branchId, string code, int capacity)
    {
        BranchId = branchId;
        Code = code.Trim().ToUpperInvariant();
        Capacity = capacity;
        IsActive = true;
    }

    public Guid TenantId { get; private set; }
    public Guid BranchId { get; private set; }
    public string Code { get; private set; } = string.Empty;
    public int Capacity { get; private set; }

    /// <summary>An inactive room cannot be booked by new or changed schedules; existing bookings stay.</summary>
    public bool IsActive { get; private set; } = true;

    Guid IAuditable.AuditSubjectId => Id;

    public static bool IsValid(string? code, int capacity) => !string.IsNullOrWhiteSpace(code) && capacity is >= 1 and <= 1000;

    public void Update(string code, int capacity)
    {
        Code = code.Trim().ToUpperInvariant();
        Capacity = capacity;
    }

    public void SetActive(bool active) => IsActive = active;
}
