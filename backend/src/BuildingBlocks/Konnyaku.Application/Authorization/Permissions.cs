namespace Konnyaku.Application.Authorization;

/// <summary>
/// What a staff member may change. Reading stays open to every staff member; writing is checked here, at the API, per
/// permission and — for branch-owned records — per branch.
/// </summary>
public static class Permissions
{
    public const string Programs = "academic.programs";
    public const string Classes = "academic.classes";
    public const string Schedules = "scheduling.schedules";
    public const string Branches = "organization.branches";
    public const string Rooms = "organization.rooms";
    public const string Holidays = "organization.holidays";
    public const string Staff = "identity.staff";
    public const string PayRates = "hr.rates";
    public const string Packages = "billing.packages";
    public const string Orders = "billing.orders";
    public const string Payments = "billing.payments";
    public const string Leads = "crm.leads";
    public const string Students = "crm.students";

    public static readonly IReadOnlyList<string> All =
    [
        Programs, Classes, Schedules, Branches, Rooms, Holidays, Staff, PayRates, Packages, Orders, Payments, Leads, Students,
    ];
}

/// <summary>Default staff roles (the same names as Identity's role list).</summary>
public static class StaffRoles
{
    public const string TenantAdmin = "TenantAdmin";
    public const string BranchManager = "BranchManager";
    public const string Coordinator = "Coordinator";
    public const string Consultant = "Consultant";
    public const string Accountant = "Accountant";
    public const string Teacher = "Teacher";

    /// <summary>Roles that may be granted for chosen branches only; every other role applies to the whole center.</summary>
    public static readonly IReadOnlySet<string> BranchScoped = new HashSet<string> { BranchManager, Coordinator };
}

/// <summary>
/// The role → permission matrix. TenantAdmin changes everything; branch managers and coordinators run classes, schedules,
/// rooms and days off of their branches; accountants handle packages, orders, payments and pay rates; consultants handle
/// leads and student records; teachers only read.
/// </summary>
public static class RolePermissions
{
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> Matrix = new Dictionary<string, IReadOnlySet<string>>
    {
        [StaffRoles.TenantAdmin] = Permissions.All.ToHashSet(),
        [StaffRoles.BranchManager] = new HashSet<string> { Permissions.Classes, Permissions.Schedules, Permissions.Rooms, Permissions.Holidays },
        [StaffRoles.Coordinator] = new HashSet<string> { Permissions.Classes, Permissions.Schedules, Permissions.Rooms, Permissions.Holidays },
        [StaffRoles.Accountant] = new HashSet<string> { Permissions.Packages, Permissions.Orders, Permissions.Payments, Permissions.PayRates },
        [StaffRoles.Consultant] = new HashSet<string> { Permissions.Leads, Permissions.Students },
        [StaffRoles.Teacher] = new HashSet<string>(),
    };

    public static bool Grants(string role, string permission) =>
        Matrix.TryGetValue(role, out var permissions) && permissions.Contains(permission);
}

/// <summary>A role held by a staff member, for the whole center (<see cref="BranchId"/> null) or for one branch.</summary>
public sealed record RoleGrant(string Role, Guid? BranchId);

/// <summary>Where a permission applies for one member: the whole center, or a list of branches.</summary>
public sealed record PermissionScope(string Permission, bool TenantWide, IReadOnlyList<Guid> BranchIds);

public static class AccessRules
{
    /// <summary>
    /// Whether <paramref name="grants"/> allow <paramref name="permission"/> on a record of <paramref name="branchId"/>.
    /// A record without a branch (center-wide day off, program) needs a center-wide grant.
    /// </summary>
    public static bool Allows(IEnumerable<RoleGrant> grants, string permission, Guid? branchId) =>
        grants.Any(g => RolePermissions.Grants(g.Role, permission) && (g.BranchId is null || (branchId is { } b && g.BranchId == b)));

    public static IReadOnlyList<PermissionScope> Scopes(IReadOnlyCollection<RoleGrant> grants) =>
        Permissions.All
            .Select(p => (Permission: p, Grants: grants.Where(g => RolePermissions.Grants(g.Role, p)).ToList()))
            .Where(x => x.Grants.Count > 0)
            .Select(x => new PermissionScope(
                x.Permission,
                x.Grants.Any(g => g.BranchId is null),
                x.Grants.Where(g => g.BranchId is not null).Select(g => g.BranchId!.Value).Distinct().ToList()))
            .ToList();
}
