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

    /// <summary>Account security of every member: lock / unlock, reset passwords and 2FA, sign-out everywhere, security log.</summary>
    public const string Accounts = "identity.accounts";
    public const string PayRates = "hr.rates";

    /// <summary>Timesheets of every teacher and payroll periods.</summary>
    public const string Payroll = "hr.payroll";
    public const string Packages = "billing.packages";
    public const string Orders = "billing.orders";
    public const string Payments = "billing.payments";

    /// <summary>Money paid back to a student (an order the class could not take, or a student leaving mid-course).</summary>
    public const string Refunds = "billing.refunds";

    /// <summary>Selling: read packages, orders, payments, refunds and lesson credits; place an order for a student.</summary>
    public const string Sales = "billing.sales";

    /// <summary>Revenue reports.</summary>
    public const string Finance = "billing.finance";
    public const string Leads = "crm.leads";
    public const string Students = "crm.students";

    /// <summary>The student list (names, branch, account; contact details need <see cref="Students"/>).</summary>
    public const string StudentsView = "crm.students.view";

    /// <summary>CSV export and printing of the student and lead lists (personal data leaving the system: logged, limited).</summary>
    public const string PersonalDataExport = "crm.export";

    /// <summary>Data subject requests (access, correction, erasure, consent withdrawal) and the personal data access log.</summary>
    public const string Privacy = "crm.privacy";

    /// <summary>The notification log of the center: read who was emailed, resend (center-wide; TenantAdmin only).</summary>
    public const string Notifications = "notification.messages";

    /// <summary>
    /// Tests and homework of every class of the branch (assignments, grading, results) and every question bank and paper.
    /// A teacher without it manages their own banks and papers and the assignments of the classes they teach.
    /// </summary>
    public const string Assessments = "assessment.manage";

    /// <summary>Every file of the center (storage used and quota, any member's uploads, quarantined files); center-wide.</summary>
    public const string Files = "content.files";

    public static readonly IReadOnlyList<string> All =
    [
        Programs, Classes, ClassesView, Schedules, Branches, Rooms, Holidays, Staff, Accounts, PayRates, Payroll, Packages, Orders, Payments,
        Refunds, Sales, Finance, Leads, Students, StudentsView, PersonalDataExport, Privacy, Notifications, Assessments, Files,
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
/// rooms, days off, tests and homework of their branches and keep student records (branch managers also follow up the leads of their
/// branches); accountants handle packages, orders, payments, refunds, pay rates and payroll and read the student list;
/// consultants handle leads and student records and place orders; teachers change nothing and see only the classes they
/// teach. Account security (locking, password and 2FA resets, security log) is the administrator's alone.
/// Personal data: lists show contact details masked; members who keep the records (<see cref="Permissions.Students"/>,
/// <see cref="Permissions.Leads"/>) reveal one record at a time (logged); only the tenant admin and branch managers export
/// or print lists; only the tenant admin handles data subject requests and reads the personal data access log.
/// </summary>
public static class RolePermissions
{
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> Matrix = new Dictionary<string, IReadOnlySet<string>>
    {
        [StaffRoles.TenantAdmin] = Permissions.All.ToHashSet(),
        [StaffRoles.BranchManager] = new HashSet<string>
        {
            Permissions.Classes, Permissions.ClassesView, Permissions.Schedules, Permissions.Rooms, Permissions.Holidays, Permissions.Leads,
            Permissions.Students, Permissions.StudentsView, Permissions.PersonalDataExport, Permissions.Assessments,
        },
        [StaffRoles.Coordinator] = new HashSet<string>
        {
            Permissions.Classes, Permissions.ClassesView, Permissions.Schedules, Permissions.Rooms, Permissions.Holidays,
            Permissions.Students, Permissions.StudentsView, Permissions.Assessments,
        },
        [StaffRoles.Accountant] = new HashSet<string>
        {
            Permissions.Packages, Permissions.Orders, Permissions.Payments, Permissions.Refunds, Permissions.Sales, Permissions.Finance,
            Permissions.PayRates, Permissions.Payroll, Permissions.ClassesView, Permissions.StudentsView,
        },
        [StaffRoles.Consultant] = new HashSet<string>
        {
            Permissions.Leads, Permissions.Students, Permissions.StudentsView, Permissions.ClassesView, Permissions.Sales,
        },
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
