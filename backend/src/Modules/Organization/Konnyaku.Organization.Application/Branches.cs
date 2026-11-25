using Konnyaku.Application.Authorization;
using Konnyaku.Organization.Contracts;
using Konnyaku.Organization.Domain;
using Konnyaku.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Konnyaku.Organization.Application;

public interface IOrganizationDbContext
{
    DbSet<Branch> Branches { get; }

    DbSet<Holiday> Holidays { get; }

    DbSet<Room> Rooms { get; }

    void Publish(IIntegrationEvent integrationEvent, string? correlationId = null);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

public sealed record CreateBranchCommand(string Code, IReadOnlyDictionary<string, string> Name, string TimeZone, bool IsVirtual);

public sealed record UpdateBranchCommand(string Code, IReadOnlyDictionary<string, string> Name, string TimeZone, bool IsVirtual);

public sealed class CreateBranchHandler(IOrganizationDbContext db)
{
    public async Task<Result<BranchDto>> HandleAsync(CreateBranchCommand command, CancellationToken cancellationToken)
    {
        var created = Branch.Create(command.Code, command.Name, command.TimeZone, command.IsVirtual);
        if (!created.IsSuccess)
        {
            return created.Error!;
        }

        var branch = created.Value;
        if (await db.Branches.AnyAsync(b => b.Code == branch.Code, cancellationToken))
        {
            return Error.Conflict("organization.branch_code_taken", new Dictionary<string, object?> { ["code"] = branch.Code });
        }

        db.Branches.Add(branch);
        db.Publish(new BranchCreatedV1(branch.Id, branch.Code, branch.TimeZone, branch.IsVirtual));
        await db.SaveChangesAsync(cancellationToken);
        return branch.ToDto();
    }
}

/// <summary>Tenant admin edits or deactivates a branch (never deleted: classes, sessions and revenue point to it).</summary>
public sealed class BranchAdminHandler(IOrganizationDbContext db)
{
    public async Task<Result<BranchDto>> UpdateAsync(Guid branchId, UpdateBranchCommand command, CancellationToken ct)
    {
        var branch = await db.Branches.FirstOrDefaultAsync(b => b.Id == branchId, ct);
        if (branch is null)
        {
            return Error.NotFound("organization.branch_unknown");
        }

        var updated = branch.Update(command.Code, command.Name, command.TimeZone, command.IsVirtual);
        if (!updated.IsSuccess)
        {
            return updated.Error!;
        }

        if (await db.Branches.AnyAsync(b => b.Id != branchId && b.Code == branch.Code, ct))
        {
            return Error.Conflict("organization.branch_code_taken", new Dictionary<string, object?> { ["code"] = branch.Code });
        }

        await db.SaveChangesAsync(ct);
        return branch.ToDto();
    }

    public async Task<Result<BranchDto>> SetActiveAsync(Guid branchId, bool active, CancellationToken ct)
    {
        var branch = await db.Branches.FirstOrDefaultAsync(b => b.Id == branchId, ct);
        if (branch is null)
        {
            return Error.NotFound("organization.branch_unknown");
        }

        branch.SetActive(active);
        await db.SaveChangesAsync(ct);
        return branch.ToDto();
    }
}

public sealed class ListBranchesHandler(IOrganizationDbContext db)
{
    public async Task<IReadOnlyList<BranchDto>> HandleAsync(CancellationToken cancellationToken)
    {
        var branches = await db.Branches.AsNoTracking().OrderBy(b => b.Code).ToListAsync(cancellationToken);
        return branches.Select(b => b.ToDto()).ToList();
    }
}

public static class BranchMapping
{
    public static BranchDto ToDto(this Branch branch) =>
        new(branch.Id, branch.Code, branch.Name.Values, branch.TimeZone, branch.IsVirtual, branch.IsActive);

    public static RoomDto ToDto(this Room room) => new(room.Id, room.BranchId, room.Code, room.Capacity, room.IsActive);

    public static HolidayDto ToDto(this Holiday holiday) => new(holiday.Id, holiday.Date, holiday.Name.Values, holiday.BranchId);
}

public sealed record AddHolidayCommand(DateOnly Date, IReadOnlyDictionary<string, string> Name, Guid? BranchId);

public sealed record AddRoomCommand(string Code, int Capacity);

public sealed record UpdateRoomCommand(string Code, int Capacity);

/// <summary>
/// Days off and rooms. Branch managers and coordinators manage those of their branches; a center-wide day off needs a
/// center-wide grant. Changing a day off does not move sessions already planned (re-plan the class schedule for that).
/// </summary>
public sealed class CalendarHandlers(IOrganizationDbContext db, IAccessControl access)
{
    public async Task<Result<HolidayDto>> AddHolidayAsync(AddHolidayCommand command, CancellationToken cancellationToken)
    {
        if (await ValidateHolidayAsync(command.Name, command.BranchId, cancellationToken) is { } invalid)
        {
            return invalid;
        }

        if (await access.EnsureAsync(Permissions.Holidays, cancellationToken, command.BranchId) is { } forbidden)
        {
            return forbidden;
        }

        var holiday = new Holiday(command.Date, new LocalizedString(command.Name), command.BranchId);
        db.Holidays.Add(holiday);
        await db.SaveChangesAsync(cancellationToken);
        return holiday.ToDto();
    }

    public async Task<Result<HolidayDto>> UpdateHolidayAsync(Guid holidayId, AddHolidayCommand command, CancellationToken ct)
    {
        var holiday = await db.Holidays.FirstOrDefaultAsync(h => h.Id == holidayId, ct);
        if (holiday is null)
        {
            return Error.NotFound("organization.holiday_not_found");
        }

        if (await ValidateHolidayAsync(command.Name, command.BranchId, ct) is { } invalid)
        {
            return invalid;
        }

        if (await access.EnsureAsync(Permissions.Holidays, ct, holiday.BranchId, command.BranchId) is { } forbidden)
        {
            return forbidden;
        }

        holiday.Update(command.Date, new LocalizedString(command.Name), command.BranchId);
        await db.SaveChangesAsync(ct);
        return holiday.ToDto();
    }

    public async Task<Result<bool>> DeleteHolidayAsync(Guid holidayId, CancellationToken ct)
    {
        var holiday = await db.Holidays.FirstOrDefaultAsync(h => h.Id == holidayId, ct);
        if (holiday is null)
        {
            return Error.NotFound("organization.holiday_not_found");
        }

        if (await access.EnsureAsync(Permissions.Holidays, ct, holiday.BranchId) is { } forbidden)
        {
            return forbidden;
        }

        db.Holidays.Remove(holiday);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<HolidayDto>> ListHolidaysAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken) =>
        (await db.Holidays.AsNoTracking().Where(h => h.Date >= from && h.Date <= to).OrderBy(h => h.Date).ToListAsync(cancellationToken))
        .Select(h => h.ToDto()).ToList();

    public async Task<Result<RoomDto>> AddRoomAsync(Guid branchId, AddRoomCommand command, CancellationToken cancellationToken)
    {
        var branch = await db.Branches.AsNoTracking().FirstOrDefaultAsync(b => b.Id == branchId, cancellationToken);
        if (branch is null)
        {
            return Error.NotFound("organization.branch_unknown");
        }

        if (await access.EnsureAsync(Permissions.Rooms, cancellationToken, branchId) is { } forbidden)
        {
            return forbidden;
        }

        if (!branch.IsActive)
        {
            return Error.Conflict("organization.branch_inactive");
        }

        if (!Room.IsValid(command.Code, command.Capacity))
        {
            return Error.Validation("organization.room_invalid");
        }

        var room = new Room(branchId, command.Code, command.Capacity);
        if (await db.Rooms.AnyAsync(x => x.BranchId == branchId && x.Code == room.Code, cancellationToken))
        {
            return Error.Conflict("organization.room_code_taken");
        }

        db.Rooms.Add(room);
        await db.SaveChangesAsync(cancellationToken);
        return room.ToDto();
    }

    public async Task<Result<RoomDto>> UpdateRoomAsync(Guid roomId, UpdateRoomCommand command, CancellationToken ct)
    {
        var room = await db.Rooms.FirstOrDefaultAsync(r => r.Id == roomId, ct);
        if (room is null)
        {
            return Error.NotFound("organization.room_not_found");
        }

        if (await access.EnsureAsync(Permissions.Rooms, ct, room.BranchId) is { } forbidden)
        {
            return forbidden;
        }

        if (!Room.IsValid(command.Code, command.Capacity))
        {
            return Error.Validation("organization.room_invalid");
        }

        room.Update(command.Code, command.Capacity);
        if (await db.Rooms.AnyAsync(x => x.Id != roomId && x.BranchId == room.BranchId && x.Code == room.Code, ct))
        {
            return Error.Conflict("organization.room_code_taken");
        }

        await db.SaveChangesAsync(ct);
        return room.ToDto();
    }

    public async Task<Result<RoomDto>> SetRoomActiveAsync(Guid roomId, bool active, CancellationToken ct)
    {
        var room = await db.Rooms.FirstOrDefaultAsync(r => r.Id == roomId, ct);
        if (room is null)
        {
            return Error.NotFound("organization.room_not_found");
        }

        if (await access.EnsureAsync(Permissions.Rooms, ct, room.BranchId) is { } forbidden)
        {
            return forbidden;
        }

        room.SetActive(active);
        await db.SaveChangesAsync(ct);
        return room.ToDto();
    }

    public async Task<IReadOnlyList<RoomDto>> ListRoomsAsync(Guid? branchId, CancellationToken ct)
    {
        var query = db.Rooms.AsNoTracking();
        if (branchId is { } id)
        {
            query = query.Where(r => r.BranchId == id);
        }

        return (await query.OrderBy(r => r.Code).ToListAsync(ct)).Select(r => r.ToDto()).ToList();
    }

    private async Task<Error?> ValidateHolidayAsync(IReadOnlyDictionary<string, string>? name, Guid? branchId, CancellationToken ct)
    {
        if (name is null || name.Values.All(string.IsNullOrWhiteSpace))
        {
            return Error.Validation("organization.holiday_name_required");
        }

        if (branchId is { } id && !await db.Branches.AnyAsync(b => b.Id == id, ct))
        {
            return Error.Validation("organization.branch_unknown");
        }

        return null;
    }
}
