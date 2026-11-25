namespace Konnyaku.Application.Authorization;

/// <summary>
/// What a staff member may change (and, for the <c>.view</c> permissions, read beyond their own teaching), checked at
/// the API per permission and — for branch-owned records — per branch.
/// </summary>
public static class Permissions
{
    public const string Programs = "academic.programs";
    public const string Classes = "academic.classes";

    /// <summary>Every class and roster of the center (a teacher without it sees only the classes they teach).</summary>
    public const string ClassesView = "academic.classes.view";
    public const string Schedules = "scheduling.schedules";
    public const string Branches = "organization.branches";
    public const string Rooms = "organization.rooms";
    public const string Holidays = "organization.holidays";
    public const string Staff = "identity.staff";
    public const string PayRates = "hr.rates";
    public const string Packages = "billing.packages";
    public const string Orders = "billing.orders";
    public const string Payments = "billing.payments";

    /// <summary>Money paid back to a student (an order the class could not take, or a student leaving mid-course).</summary>
    public const string Refunds = "billing.refunds";
    public const string Leads = "crm.leads";
    public const string Students = "crm.students";

    /// <summary>The student list (names, branch, account; contact details need <see cref="Students"/>).</summary>
    public const string StudentsView = "crm.students.view";

    /// <summary>The notification log of the center: read who was emailed, resend (center-wide; TenantAdmin only).</summary>
    public const string Notifications = "notification.messages";

    public static readonly IReadOnlyList<string> All =
    [
        Programs, Classes, ClassesView, Schedules, Branches, Rooms, Holidays, Staff, PayRates, Packages, Orders, Payments, Refunds,
        Leads, Students, StudentsView, Notifications,
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

    /// <summary>
    /// Roles that may be granted for chosen branches only; every other role applies to the whole center. A branch accountant
    /// handles the orders, payments and refunds of their branches; packages and pay rates stay center-wide.
    /// </summary>
    public static readonly IReadOnlySet<string> BranchScoped = new HashSet<string> { BranchManager, Coordinator, Accountant };
}

/// <summary>
/// The role → permission matrix. TenantAdmin changes everything; branch managers and coordinators run classes, schedules,
/// rooms and days off of their branches and keep student records (branch managers also follow up the leads of their
/// branches); accountants handle packages, orders, payments, refunds and pay rates and read the student list; consultants handle
/// leads and student records; teachers change nothing and see only the classes they teach.
/// </summary>
public static class RolePermissions
{
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> Matrix = new Dictionary<string, IReadOnlySet<string>>
    {
        [StaffRoles.TenantAdmin] = Permissions.All.ToHashSet(),
        [StaffRoles.BranchManager] = new HashSet<string>
        {
            Permissions.Classes, Permissions.ClassesView, Permissions.Schedules, Permissions.Rooms, Permissions.Holidays, Permissions.Leads,
            Permissions.Students, Permissions.StudentsView,
        },
        [StaffRoles.Coordinator] = new HashSet<string>
        {
            Permissions.Classes, Permissions.ClassesView, Permissions.Schedules, Permissions.Rooms, Permissions.Holidays,
            Permissions.Students, Permissions.StudentsView,
        },
        [StaffRoles.Accountant] = new HashSet<string>
        {
            Permissions.Packages, Permissions.Orders, Permissions.Payments, Permissions.Refunds, Permissions.PayRates, Permissions.ClassesView,
            Permissions.StudentsView,
        },
        [StaffRoles.Consultant] = new HashSet<string> { Permissions.Leads, Permissions.Students, Permissions.StudentsView, Permissions.ClassesView },
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
