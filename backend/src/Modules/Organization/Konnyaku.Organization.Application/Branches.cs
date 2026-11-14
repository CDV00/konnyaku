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
        new(branch.Id, branch.Code, branch.Name.Values, branch.TimeZone, branch.IsVirtual);
}

public sealed record AddHolidayCommand(DateOnly Date, IReadOnlyDictionary<string, string> Name, Guid? BranchId);

public sealed record AddRoomCommand(string Code, int Capacity);

public sealed class CalendarHandlers(IOrganizationDbContext db)
{
    public async Task<Result<HolidayDto>> AddHolidayAsync(AddHolidayCommand command, CancellationToken cancellationToken)
    {
        if (command.Name is null || command.Name.Values.All(string.IsNullOrWhiteSpace))
        {
            return Error.Validation("organization.holiday_name_required");
        }

        if (command.BranchId is { } branchId && !await db.Branches.AnyAsync(b => b.Id == branchId, cancellationToken))
        {
            return Error.Validation("organization.branch_unknown");
        }

        var holiday = new Holiday(command.Date, new LocalizedString(command.Name), command.BranchId);
        db.Holidays.Add(holiday);
        await db.SaveChangesAsync(cancellationToken);
        return new HolidayDto(holiday.Id, holiday.Date, holiday.Name.Values, holiday.BranchId);
    }

    public async Task<IReadOnlyList<HolidayDto>> ListHolidaysAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken) =>
        (await db.Holidays.AsNoTracking().Where(h => h.Date >= from && h.Date <= to).OrderBy(h => h.Date).ToListAsync(cancellationToken))
        .Select(h => new HolidayDto(h.Id, h.Date, h.Name.Values, h.BranchId)).ToList();

    public async Task<Result<RoomDto>> AddRoomAsync(Guid branchId, AddRoomCommand command, CancellationToken cancellationToken)
    {
        if (!await db.Branches.AnyAsync(b => b.Id == branchId, cancellationToken))
        {
            return Error.NotFound("organization.branch_unknown");
        }

        if (string.IsNullOrWhiteSpace(command.Code) || command.Capacity is < 1 or > 1000)
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
        return new RoomDto(room.Id, room.BranchId, room.Code, room.Capacity);
    }
}
