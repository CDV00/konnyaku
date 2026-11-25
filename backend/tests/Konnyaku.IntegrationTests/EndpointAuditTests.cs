using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Konnyaku.IntegrationTests;

/// <summary>
/// Endpoints that only checked "is staff" (or the roles written in the token) before the P3 review, now checked against
/// the live permission matrix: one test per fix.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class EndpointAuditTests(PostgresFixture db) : IAsyncDisposable
{
    private readonly KonnyakuApiFactory _api = new(db);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _api.DisposeAsync();

    [Fact]
    public async Task Class_schedules_are_visible_to_their_teacher_and_to_class_roles_only()
    {
        var c = await EditCenter.CreateAsync(_api, "audsch");
        var classA = await c.CreateClassAsync("CLS-A", c.BranchA);
        var classB = await c.CreateClassAsync("CLS-B", c.BranchB);
        await c.ScheduleAsync(classA, [1], "18:00", c.TeacherId);
        await c.ScheduleAsync(classB, [2], "18:00", c.OtherTeacherId);
        var teacher = await c.SignInAsync(c.TeacherId);
        var (_, accountant) = await c.AddStaffAsync("Accountant");

        (await teacher.GetAsync($"/api/scheduling/classes/{classA}/sessions", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await teacher.GetAsync($"/api/scheduling/classes/{classA}/pattern", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await (await teacher.GetAsync($"/api/scheduling/classes/{classB}/sessions", Ct)).ShouldFailWith(HttpStatusCode.Forbidden, "access.forbidden");
        await (await teacher.GetAsync($"/api/scheduling/classes/{classB}/pattern", Ct)).ShouldFailWith(HttpStatusCode.Forbidden, "access.forbidden");
        (await accountant.GetAsync($"/api/scheduling/classes/{classB}/sessions", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await (await teacher.GetAsync($"/api/scheduling/history/{classA}", Ct)).ShouldFailWith(HttpStatusCode.Forbidden, "access.forbidden");
    }

    [Fact]
    public async Task Attendance_is_for_the_session_teacher_and_whoever_runs_its_branch()
    {
        var c = await EditCenter.CreateAsync(_api, "audatt");
        var classA = await c.CreateClassAsync("CLS-A", c.BranchA);
        var classB = await c.CreateClassAsync("CLS-B", c.BranchB);
        var sessionA = (await c.ScheduleAsync(classA, [1], "18:00", c.TeacherId))[0];
        var sessionB = (await c.ScheduleAsync(classB, [2], "18:00", c.OtherTeacherId))[0];
        var teacher = await c.SignInAsync(c.TeacherId);
        var (_, managerA) = await c.AddStaffAsync("BranchManager", c.BranchA);
        var (_, coordinatorA) = await c.AddStaffAsync("Coordinator", c.BranchA);
        var (_, consultant) = await c.AddStaffAsync("Consultant");

        (await teacher.GetAsync($"/api/learning/sessions/{sessionA.Id}/attendance", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await teacher.GetAsync($"/api/learning/sessions/{sessionB.Id}/attendance", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        // Branch managers can now take attendance of their branch (they could not before); a branch's coordinator only there.
        (await managerA.GetAsync($"/api/learning/sessions/{sessionA.Id}/attendance", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await coordinatorA.GetAsync($"/api/learning/sessions/{sessionA.Id}/attendance", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await coordinatorA.GetAsync($"/api/learning/sessions/{sessionB.Id}/attendance", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await consultant.GetAsync($"/api/learning/sessions/{sessionA.Id}/attendance", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await c.Admin.GetAsync($"/api/learning/sessions/{sessionB.Id}/attendance", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Money_and_payroll_follow_the_permission_matrix()
    {
        var c = await EditCenter.CreateAsync(_api, "audbil");
        var (_, accountant) = await c.AddStaffAsync("Accountant");
        var (_, consultant) = await c.AddStaffAsync("Consultant");
        var (_, coordinator) = await c.AddStaffAsync("Coordinator");
        var teacher = await c.SignInAsync(c.TeacherId);

        foreach (var seller in new[] { accountant, consultant })
        {
            (await seller.GetAsync("/api/billing/packages", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
            (await seller.GetAsync("/api/billing/orders", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        foreach (var other in new[] { coordinator, teacher })
        {
            await (await other.GetAsync("/api/billing/packages", Ct)).ShouldFailWith(HttpStatusCode.Forbidden, "access.forbidden");
            await (await other.GetAsync("/api/billing/orders", Ct)).ShouldFailWith(HttpStatusCode.Forbidden, "access.forbidden");
        }

        (await accountant.GetAsync("/api/billing/reports/recognized-revenue?from=2027-01-01&to=2027-01-31", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await (await consultant.GetAsync("/api/billing/reports/recognized-revenue?from=2027-01-01&to=2027-01-31", Ct))
            .ShouldFailWith(HttpStatusCode.Forbidden, "access.forbidden");
        (await accountant.GetAsync("/api/hr/payroll-periods", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await accountant.GetAsync("/api/hr/timesheets?from=2027-01-01&to=2027-01-31", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await (await consultant.GetAsync("/api/hr/payroll-periods", Ct)).ShouldFailWith(HttpStatusCode.Forbidden, "access.forbidden");
        await (await teacher.GetAsync("/api/hr/timesheets?from=2027-01-01&to=2027-01-31", Ct)).ShouldFailWith(HttpStatusCode.Forbidden, "access.forbidden");
        (await teacher.GetAsync("/api/hr/my/timesheets?from=2027-01-01&to=2027-01-31", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await (await teacher.GetAsync($"/api/billing/history/{Guid.NewGuid()}", Ct)).ShouldFailWith(HttpStatusCode.Forbidden, "access.forbidden");
        await (await teacher.GetAsync($"/api/hr/history/{c.TeacherId}", Ct)).ShouldFailWith(HttpStatusCode.Forbidden, "access.forbidden");
    }

    [Fact]
    public async Task Learner_accounts_are_issued_by_whoever_keeps_student_records()
    {
        var c = await EditCenter.CreateAsync(_api, "audlrn");
        var (_, coordinator) = await c.AddStaffAsync("Coordinator", c.BranchA);
        var (_, accountant) = await c.AddStaffAsync("Accountant");
        var teacher = await c.SignInAsync(c.TeacherId);
        object Learner() => new { displayName = "Học viên", role = "Student" };

        (await coordinator.PostAsJsonAsync("/api/identity/learners", Learner(), Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        await (await accountant.PostAsJsonAsync("/api/identity/learners", Learner(), Ct)).ShouldFailWith(HttpStatusCode.Forbidden, "access.forbidden");
        await (await teacher.PostAsJsonAsync("/api/identity/learners", Learner(), Ct)).ShouldFailWith(HttpStatusCode.Forbidden, "access.forbidden");
        await (await teacher.GetAsync($"/api/identity/history/{c.TeacherId}", Ct)).ShouldFailWith(HttpStatusCode.Forbidden, "access.forbidden");
    }

    [Fact]
    public async Task The_meeting_simulator_is_off_unless_dev_tools_are_enabled()
    {
        var c = await EditCenter.CreateAsync(_api, "audsim");
        var classA = await c.CreateClassAsync("CLS-A", c.BranchA);
        var session = (await c.ScheduleAsync(classA, [1], "18:00", c.TeacherId))[0];
        var teacher = await c.SignInAsync(c.TeacherId);
        var body = new { participants = Array.Empty<object>() };

        // Enabled (tests, Development): planners only.
        await (await teacher.PostAsJsonAsync($"/api/workspace/dev/sessions/{session.Id}/simulate", body, Ct))
            .ShouldFailWith(HttpStatusCode.Forbidden, "access.forbidden");

        await using var production = _api.WithWebHostBuilder(b => b.UseSetting("Workspace:DevTools", "false"));
        var admin = production.CreateClient();
        foreach (var header in c.Admin.DefaultRequestHeaders)
        {
            admin.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
        }

        (await admin.PostAsJsonAsync($"/api/workspace/dev/sessions/{session.Id}/simulate", body, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
