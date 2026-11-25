using Konnyaku.Academic.Domain;
using Konnyaku.Application.Authorization;

namespace Konnyaku.UnitTests;

public class AccessRulesTests
{
    private static readonly Guid BranchA = Guid.CreateVersion7();
    private static readonly Guid BranchB = Guid.CreateVersion7();

    [Theory]
    [InlineData("TenantAdmin", Permissions.Branches, true)]
    [InlineData("TenantAdmin", Permissions.Payments, true)]
    [InlineData("TenantAdmin", Permissions.Notifications, true)]
    [InlineData("BranchManager", Permissions.Notifications, false)]
    [InlineData("BranchManager", Permissions.Classes, true)]
    [InlineData("BranchManager", Permissions.Programs, false)]
    [InlineData("Coordinator", Permissions.Holidays, true)]
    [InlineData("Coordinator", Permissions.Packages, false)]
    [InlineData("Accountant", Permissions.Orders, true)]
    [InlineData("Accountant", Permissions.Classes, false)]
    [InlineData("Consultant", Permissions.Students, true)]
    [InlineData("Consultant", Permissions.Rooms, false)]
    [InlineData("Teacher", Permissions.Classes, false)]
    [InlineData("Teacher", Permissions.Leads, false)]
    [InlineData("Teacher", Permissions.ClassesView, false)]
    [InlineData("Teacher", Permissions.StudentsView, false)]
    [InlineData("Accountant", Permissions.StudentsView, true)]
    [InlineData("Accountant", Permissions.Students, false)]
    [InlineData("BranchManager", Permissions.Leads, true)]
    [InlineData("Coordinator", Permissions.Leads, false)]
    [InlineData("Coordinator", Permissions.Students, true)]
    public void Role_matrix(string role, string permission, bool granted) =>
        RolePermissions.Grants(role, permission).ShouldBe(granted);

    [Fact]
    public void A_branch_grant_covers_its_branch_only_and_never_center_wide_records()
    {
        RoleGrant[] grants = [new("Coordinator", BranchA)];
        AccessRules.Allows(grants, Permissions.Rooms, BranchA).ShouldBeTrue();
        AccessRules.Allows(grants, Permissions.Rooms, BranchB).ShouldBeFalse();
        AccessRules.Allows(grants, Permissions.Holidays, null).ShouldBeFalse();
    }

    [Fact]
    public void A_center_wide_grant_covers_every_branch_and_center_wide_records()
    {
        RoleGrant[] grants = [new("BranchManager", null)];
        AccessRules.Allows(grants, Permissions.Classes, BranchB).ShouldBeTrue();
        AccessRules.Allows(grants, Permissions.Holidays, null).ShouldBeTrue();
    }

    [Fact]
    public void Scopes_list_where_each_permission_applies()
    {
        var scopes = AccessRules.Scopes([new("Coordinator", BranchA), new("Coordinator", BranchB), new("Accountant", null)]);
        scopes.Single(s => s.Permission == Permissions.Schedules).ShouldSatisfyAllConditions(
            s => s.TenantWide.ShouldBeFalse(),
            s => s.BranchIds.ShouldBe([BranchA, BranchB], ignoreOrder: true));
        scopes.Single(s => s.Permission == Permissions.Payments).TenantWide.ShouldBeTrue();
        scopes.ShouldNotContain(s => s.Permission == Permissions.Staff);
    }
}

public class ClassEditRulesTests
{
    private static Class NewClass() =>
        Class.Create("CLS-1", Guid.CreateVersion7(), Guid.CreateVersion7(), DeliveryMode.Online, 10, new DateOnly(2027, 1, 4), "Asia/Ho_Chi_Minh").Value;

    private static ClassChange Same(Class c) => new(c.Code, c.CourseLevelId, c.BranchId, c.TimeZone, c.DeliveryMode, c.Capacity, c.StartsOn);

    [Fact]
    public void Structure_is_free_before_scheduling_and_locked_after()
    {
        var cls = NewClass();
        var moved = Same(cls) with { BranchId = Guid.CreateVersion7(), StartsOn = new DateOnly(2027, 2, 1) };
        cls.Update(moved, hasSessions: true).Error!.Code.ShouldBe("academic.class_branch_locked");
        cls.Update(moved with { BranchId = cls.BranchId }, hasSessions: true).Error!.Code.ShouldBe("academic.class_start_locked");
        cls.Update(moved, hasSessions: false).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Closed_classes_take_no_edits_until_reopened()
    {
        var cls = NewClass();
        cls.Close(ClassStatus.Completed).IsSuccess.ShouldBeTrue();
        cls.Update(Same(cls) with { Capacity = 12 }, hasSessions: false).Error!.Code.ShouldBe("academic.class_closed");
        cls.Close(ClassStatus.Cancelled).Error!.Code.ShouldBe("academic.class_closed");
        cls.Reopen().IsSuccess.ShouldBeTrue();
        cls.Update(Same(cls) with { Capacity = 12 }, hasSessions: false).IsSuccess.ShouldBeTrue();
    }
}
