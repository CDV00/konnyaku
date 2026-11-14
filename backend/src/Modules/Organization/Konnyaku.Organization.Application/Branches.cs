using Konnyaku.Organization.Contracts;
using Konnyaku.Organization.Domain;
using Konnyaku.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Konnyaku.Organization.Application;

public interface IOrganizationDbContext
{
    DbSet<Branch> Branches { get; }

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
