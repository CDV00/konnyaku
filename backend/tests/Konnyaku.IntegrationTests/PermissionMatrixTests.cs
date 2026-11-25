using System.Net;
using System.Net.Http.Json;

namespace Konnyaku.IntegrationTests;

/// <summary>
/// Role × branch matrix, checked at the API: TenantAdmin changes everything; BranchManager and Coordinator change
/// classes, schedules, rooms and days off of their own branches; Accountant packages, orders and payments; Consultant
/// leads and students; Teacher nothing.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PermissionMatrixTests(PostgresFixture db) : IAsyncDisposable
{
    private readonly KonnyakuApiFactory _api = new(db);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _api.DisposeAsync();

    [Theory]
    [InlineData("BranchManager")]
    [InlineData("Coordinator")]
    public async Task Branch_roles_change_their_branch_only(string role)
    {
        var c = await EditCenter.CreateAsync(_api, "permbr");
        var (_, member) = await c.AddStaffAsync(role, c.BranchA);
        var classA = await c.CreateClassAsync("CLS-A", c.BranchA);
        var classB = await c.CreateClassAsync("CLS-B", c.BranchB);

        object Edit(Guid branch, int capacity = 20, string code = "CLS-A") =>
            new { code, courseLevelId = c.LevelId, branchId = branch, deliveryMode = "Online", capacity, startsOn = "2027-01-04" };

        // Classes
        await (await member.PutAsJsonAsync($"/api/academic/classes/{classA}", Edit(c.BranchA), Ct)).ShouldSucceed();
        await (await member.PutAsJsonAsync($"/api/academic/classes/{classB}", Edit(c.BranchB, code: "CLS-B"), Ct))
            .ShouldFailWith(HttpStatusCode.Forbidden, "access.branch_forbidden");
        await (await member.PutAsJsonAsync($"/api/academic/classes/{classA}", Edit(c.BranchB), Ct))
            .ShouldFailWith(HttpStatusCode.Forbidden, "access.branch_forbidden");
        await (await member.PostAsJsonAsync("/api/academic/classes",
                new { code = "CLS-C", courseLevelId = c.LevelId, branchId = c.BranchB, deliveryMode = "Online", capacity = 5, startsOn = "2027-01-04" }, Ct))
            .ShouldFailWith(HttpStatusCode.Forbidden, "access.branch_forbidden");

        // Schedules
        await (await member.PostAsJsonAsync($"/api/scheduling/classes/{classA}/schedule", new { daysOfWeek = new[] { 1 }, startTime = "18:00", teacherId = c.TeacherId }, Ct))
            .ShouldSucceed();
        await (await member.PutAsJsonAsync($"/api/scheduling/classes/{classA}/schedule", new { daysOfWeek = new[] { 2 }, startTime = "18:00", teacherId = c.TeacherId }, Ct))
            .ShouldSucceed();
        await (await member.PostAsJsonAsync($"/api/scheduling/classes/{classB}/schedule", new { daysOfWeek = new[] { 1 }, startTime = "18:00" }, Ct))
            .ShouldFailWith(HttpStatusCode.Forbidden, "access.branch_forbidden");
        var sessionB = (await c.ScheduleAsync(classB, [3], "18:00", null))[0];
        await (await member.PostAsJsonAsync($"/api/scheduling/sessions/{sessionB.Id}/cancel", new { reason = "x" }, Ct))
            .ShouldFailWith(HttpStatusCode.Forbidden, "access.branch_forbidden");

        // Rooms and days off
        await (await member.PostAsJsonAsync($"/api/organization/branches/{c.BranchA}/rooms", new { code = "P1", capacity = 10 }, Ct)).ShouldSucceed();
        await (await member.PostAsJsonAsync($"/api/organization/branches/{c.BranchB}/rooms", new { code = "P1", capacity = 10 }, Ct))
            .ShouldFailWith(HttpStatusCode.Forbidden, "access.branch_forbidden");
        var roomB = await c.PostIdAsync($"/api/organization/branches/{c.BranchB}/rooms", new { code = "P9", capacity = 10 });
        await (await member.PutAsJsonAsync($"/api/organization/rooms/{roomB}", new { code = "P9", capacity = 12 }, Ct))
            .ShouldFailWith(HttpStatusCode.Forbidden, "access.branch_forbidden");
        await (await member.PostAsJsonAsync("/api/organization/holidays", new { date = "2027-02-01", name = new { vi = "Nghỉ" }, branchId = c.BranchA }, Ct)).ShouldSucceed();
        await (await member.PostAsJsonAsync("/api/organization/holidays", new { date = "2027-02-01", name = new { vi = "Nghỉ" }, branchId = (Guid?)null }, Ct))
            .ShouldFailWith(HttpStatusCode.Forbidden, "access.branch_forbidden");
        await (await member.PostAsJsonAsync("/api/organization/holidays", new { date = "2027-02-01", name = new { vi = "Nghỉ" }, branchId = c.BranchB }, Ct))
            .ShouldFailWith(HttpStatusCode.Forbidden, "access.branch_forbidden");

        // Hand-made enrollments follow the class's branch.
        var studentId = await c.PostIdAsync("/api/crm/students", new { fullName = "HV" });
        (await member.PostAsJsonAsync($"/api/academic/classes/{classA}/enrollments", new { studentId, type = "Regular" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        await (await member.PostAsJsonAsync($"/api/academic/classes/{classB}/enrollments", new { studentId, type = "Regular" }, Ct))
            .ShouldFailWith(HttpStatusCode.Forbidden, "access.branch_forbidden");
        var enrollmentB = (await (await c.Admin.PostAsJsonAsync($"/api/academic/classes/{classB}/enrollments", new { studentId, type = "Regular" }, Ct))
            .Content.ReadFromJsonAsync<EnrollmentView>(Ct))!.EnrollmentId;
        await (await member.PostAsJsonAsync($"/api/academic/enrollments/{enrollmentB}/drop", new { }, Ct))
            .ShouldFailWith(HttpStatusCode.Forbidden, "access.branch_forbidden");

        // Student records are center-wide: branch roles keep them.
        await (await member.PutAsJsonAsync($"/api/crm/students/{studentId}", new { fullName = "Học viên A" }, Ct)).ShouldSucceed();

        // Nothing outside the role
        (await member.PutAsJsonAsync($"/api/organization/branches/{c.BranchA}", new { code = "CS-A", name = new { vi = "A" }, timeZone = "Asia/Ho_Chi_Minh", isVirtual = false }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await member.PostAsJsonAsync("/api/billing/packages", Package(), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await member.PutAsJsonAsync($"/api/academic/levels/{c.LevelId}", Level(), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_branch_manager_for_the_whole_center_changes_every_branch()
    {
        var c = await EditCenter.CreateAsync(_api, "permall");
        var (_, manager) = await c.AddStaffAsync("BranchManager");
        var classB = await c.CreateClassAsync("CLS-B", c.BranchB);
        await (await manager.PutAsJsonAsync($"/api/academic/classes/{classB}",
            new { code = "CLS-B", courseLevelId = c.LevelId, branchId = c.BranchB, deliveryMode = "Hybrid", capacity = 12, startsOn = "2027-01-04" }, Ct)).ShouldSucceed();
        await (await manager.PostAsJsonAsync("/api/organization/holidays", new { date = "2027-02-01", name = new { vi = "Tết" }, branchId = (Guid?)null }, Ct)).ShouldSucceed();
    }

    [Fact]
    public async Task Accountant_consultant_and_teacher_stay_in_their_lane()
    {
        var c = await EditCenter.CreateAsync(_api, "permfin");
        var (_, accountant) = await c.AddStaffAsync("Accountant");
        var (_, consultant) = await c.AddStaffAsync("Consultant");
        var (teacherId, teacher) = await c.AddStaffAsync("Teacher");
        var classA = await c.CreateClassAsync("CLS-A", c.BranchA);
        var classB = await c.CreateClassAsync("CLS-B", c.BranchB);
        await c.ScheduleAsync(classA, [1], "18:00", teacherId);
        var studentId = await c.PostIdAsync("/api/crm/students", new { fullName = "HV" });
        var package = await c.PostIdAsync("/api/billing/packages", Package());
        var order = await c.PostIdAsync("/api/billing/orders", new { studentId, packageId = package });
        var classEdit = new { code = "CLS-A", courseLevelId = c.LevelId, branchId = c.BranchA, deliveryMode = "Online", capacity = 20, startsOn = "2027-01-04" };
        var studentEdit = new { fullName = "Học viên" };

        // Accountant: packages, orders, payments, pay rates; reads the student list.
        (await accountant.GetAsync("/api/crm/students", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await accountant.GetAsync("/api/crm/leads", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await (await accountant.PutAsJsonAsync($"/api/billing/packages/{package}", Package(price: 900000), Ct)).ShouldSucceed();
        await (await accountant.PutAsJsonAsync($"/api/billing/orders/{order}", new { amount = 800000 }, Ct)).ShouldSucceed();
        await (await accountant.PostAsJsonAsync($"/api/billing/orders/{order}/payments", new { amount = 100000, method = "cash" }, Ct)).ShouldSucceed();
        (await accountant.PutAsJsonAsync($"/api/academic/classes/{classA}", classEdit, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await accountant.PutAsJsonAsync($"/api/crm/students/{studentId}", studentEdit, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Consultant: leads and students.
        await (await consultant.PutAsJsonAsync($"/api/crm/students/{studentId}", studentEdit, Ct)).ShouldSucceed();
        (await consultant.PutAsJsonAsync($"/api/billing/packages/{package}", Package(), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await consultant.PutAsJsonAsync($"/api/academic/classes/{classA}", classEdit, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Teacher: sees only the classes they teach, no student list, no leads, changes nothing.
        (await teacher.GetFromJsonAsync<IdView[]>("/api/academic/classes", Ct))!.Select(x => x.Id).ShouldBe([classA]);
        (await teacher.GetAsync($"/api/academic/classes/{classA}/roster", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await teacher.GetAsync($"/api/academic/classes/{classB}/roster", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await teacher.GetAsync("/api/crm/students", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await teacher.GetAsync("/api/crm/leads", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await c.Admin.GetFromJsonAsync<IdView[]>("/api/academic/classes", Ct))!.Length.ShouldBe(2);
        (await teacher.GetAsync($"/api/academic/history/{classA}", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await teacher.GetAsync("/api/identity/staff", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await accountant.GetAsync("/api/identity/staff", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await teacher.PutAsJsonAsync($"/api/academic/classes/{classA}", classEdit, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await teacher.PutAsJsonAsync($"/api/crm/students/{studentId}", studentEdit, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await teacher.PutAsJsonAsync($"/api/billing/packages/{package}", Package(), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await teacher.PostAsJsonAsync($"/api/organization/branches/{c.BranchA}/rooms", new { code = "P1", capacity = 5 }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await teacher.PostAsJsonAsync($"/api/academic/classes/{classA}/enrollments", new { studentId, type = "Regular" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await teacher.PutAsJsonAsync($"/api/identity/staff/{c.TeacherId}", new { displayName = "x", roles = new[] { new { role = "TenantAdmin" } } }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var access = (await teacher.GetFromJsonAsync<object[]>("/api/identity/me/access", Ct))!;
        access.ShouldBeEmpty();

        // Only the tenant admin manages staff, programs and branches.
        foreach (var client in new[] { accountant, consultant })
        {
            (await client.PostAsJsonAsync($"/api/academic/programs/{c.ProgramId}/deactivate", new { }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await client.PostAsJsonAsync($"/api/organization/branches/{c.BranchA}/deactivate", new { }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await client.PostAsJsonAsync($"/api/identity/staff/{c.TeacherId}/deactivate", new { }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }
    }

    [Fact]
    public async Task Another_tenant_cannot_see_or_change_records()
    {
        var mine = await EditCenter.CreateAsync(_api, "permt1");
        var theirs = await EditCenter.CreateAsync(_api, "permt2");
        var classId = await mine.CreateClassAsync("CLS-A", mine.BranchA);

        await (await theirs.Admin.PutAsJsonAsync($"/api/academic/classes/{classId}",
                new { code = "CLS-A", courseLevelId = theirs.LevelId, branchId = theirs.BranchA, deliveryMode = "Online", capacity = 20, startsOn = "2027-01-04" }, Ct))
            .ShouldFailWith(HttpStatusCode.NotFound, "academic.class_not_found");
        (await theirs.HistoryAsync("academic", classId)).ShouldBeEmpty();
        (await mine.HistoryAsync("academic", classId)).ShouldNotBeEmpty();
    }

    [Fact]
    public async Task A_branch_manager_follows_up_the_leads_of_their_branches_only()
    {
        var c = await EditCenter.CreateAsync(_api, "permlead");
        var (_, manager) = await c.AddStaffAsync("BranchManager", c.BranchA);
        var (_, coordinator) = await c.AddStaffAsync("Coordinator", c.BranchA);
        var visitor = _api.ClientFor(c.Slug);
        foreach (var (phone, branch) in new[] { ("0911000001", c.BranchA), ("0911000002", c.BranchB) })
        {
            (await visitor.PostAsJsonAsync("/api/crm/public/leads", new
            {
                studentName = $"Lead {phone}", phone, languageCode = "en", preferredBranchId = branch, consentVersion = "v1", consentGiven = true,
            }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        var all = (await c.Admin.GetFromJsonAsync<LeadView[]>("/api/crm/leads", Ct))!;
        all.Length.ShouldBe(2);
        var mine = (await manager.GetFromJsonAsync<LeadView[]>("/api/crm/leads", Ct))!;
        mine.Select(l => l.PreferredBranchId).ShouldBe([c.BranchA]);

        var leadA = all.Single(l => l.PreferredBranchId == c.BranchA).Id;
        var leadB = all.Single(l => l.PreferredBranchId == c.BranchB).Id;
        await (await manager.PostAsJsonAsync($"/api/crm/leads/{leadA}/status", new { status = "Contacted" }, Ct)).ShouldSucceed();
        await (await manager.PostAsJsonAsync($"/api/crm/leads/{leadB}/status", new { status = "Contacted" }, Ct))
            .ShouldFailWith(HttpStatusCode.Forbidden, "access.branch_forbidden");
        await (await manager.PutAsJsonAsync($"/api/crm/leads/{leadA}",
                new { studentName = "Lead A", phone = "0911000001", languageCode = "en", preferredBranchId = c.BranchB }, Ct))
            .ShouldFailWith(HttpStatusCode.Forbidden, "access.branch_forbidden");

        // Coordinators keep student records but do not handle leads.
        (await coordinator.GetAsync("/api/crm/leads", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await coordinator.PostAsJsonAsync("/api/crm/students", new { fullName = "HV mới" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    private sealed record LeadView(Guid Id, Guid? PreferredBranchId);

    private sealed record EnrollmentView(Guid EnrollmentId);

    private static object Package(decimal price = 1000000) =>
        new { code = "P10", name = new { vi = "10 buổi" }, paidCredits = 10, bonusCredits = 0, price, currency = "VND" };

    private static object Level() =>
        new { code = "A1", name = new { vi = "A1" }, targetLevelCode = "A1", sessionCount = 4, sessionMinutes = 60 };
}
