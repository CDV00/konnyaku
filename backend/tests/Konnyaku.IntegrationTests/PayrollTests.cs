using System.Net;
using System.Net.Http.Json;

namespace Konnyaku.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class PayrollTests(PostgresFixture db) : IAsyncDisposable
{
    private const string Password = "Admin-Pass-123";
    private readonly KonnyakuApiFactory _api = new(db);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _api.DisposeAsync();

    [Fact]
    public async Task Timesheets_come_from_real_teaching_and_payroll_periods_freeze_them()
    {
        var email = $"admin-{Guid.NewGuid():N}@example.com";
        var tenant = await _api.CreateTenantWithAdminAsync("pay", email, Password);
        var admin = _api.ClientFor(tenant.Slug, (await _api.StaffLoginAsync(tenant.Slug, email, Password)).AccessToken);

        async Task<Guid> Post(string path, object body, string key = "id")
        {
            var response = await admin.PostAsJsonAsync(path, body, Ct);
            response.IsSuccessStatusCode.ShouldBeTrue($"{path}: {await response.Content.ReadAsStringAsync(Ct)}");
            return Guid.Parse((await response.Content.ReadFromJsonAsync<Dictionary<string, object>>(Ct))![key].ToString()!);
        }

        (await admin.PutAsJsonAsync("/api/workspace/connection", new { provider = "Fake" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await Post("/api/workspace/hosts", new { email = "host@example.com" });
        var teacherEmail = $"t-{Guid.NewGuid():N}@example.com";
        var teacher = await Post("/api/identity/staff", new { email = teacherEmail, displayName = "GV Lương", initialPassword = Password, roles = new[] { "Teacher" } }, "userId");
        (await admin.PutAsJsonAsync($"/api/hr/teachers/{teacher}/rate", new { hourlyRate = 300_000m, currency = "VND", effectiveFrom = "2027-01-01" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var branch = await Post("/api/organization/branches", new { code = "Q1", name = new { vi = "Q1" }, timeZone = "Asia/Ho_Chi_Minh", isVirtual = false });
        var program = await Post("/api/academic/programs", new { code = "EN", name = new { vi = "EN" }, languageCode = "en", frameworkCode = "CEFR", isPublic = false });
        var level = await Post($"/api/academic/programs/{program}/levels", new { code = "A1", name = new { vi = "A1" }, targetLevelCode = "A1", sessionCount = 2, sessionMinutes = 90 });

        async Task<SessionView[]> ClassAsync(string code, string mode, int isoDay)
        {
            var cls = await Post("/api/academic/classes", new { code, courseLevelId = level, branchId = branch, deliveryMode = mode, capacity = 10, startsOn = "2027-03-01" });
            (await admin.PostAsJsonAsync($"/api/scheduling/classes/{cls}/schedule",
                new { daysOfWeek = new[] { isoDay }, startTime = "19:00", teacherId = teacher }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
            await _api.DrainOutboxAsync();
            return (await admin.GetFromJsonAsync<SessionView[]>($"/api/scheduling/classes/{cls}/sessions", Ct))!;
        }

        var online = await ClassAsync("ON-01", "Online", 2);
        var offline = await ClassAsync("OFF-01", "Offline", 4);

        // Online lesson: the teacher joins 10 minutes late and stays to the end -> 80 paid minutes.
        (await admin.PostAsJsonAsync($"/api/workspace/dev/sessions/{online[0].Id}/simulate", new
        {
            participants = new[] { new { userId = (Guid?)teacher, displayName = "GV", joinedMinute = 10, leftMinute = 90 } },
            recording = false,
        }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        // Offline lesson: no meeting log; attendance confirmed by hand pays the planned 90 minutes.
        var teacherClient = _api.ClientFor(tenant.Slug, (await _api.StaffLoginAsync(tenant.Slug, teacherEmail, Password)).AccessToken);
        (await teacherClient.PostAsJsonAsync($"/api/learning/sessions/{offline[0].Id}/attendance/confirm", new { overrides = Array.Empty<object>() }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        await _api.DrainOutboxAsync();

        var mine = (await teacherClient.GetFromJsonAsync<TimesheetView[]>("/api/hr/my/timesheets?from=2027-03-01&to=2027-03-31", Ct))!;
        mine.Length.ShouldBe(2);
        var meeting = mine.Single(t => t.SessionId == online[0].Id);
        (meeting.Source, meeting.PaidMinutes, meeting.LateMinutes, meeting.Amount).ShouldBe(("Meeting", 80, 10, 400_000m));
        var planned = mine.Single(t => t.SessionId == offline[0].Id);
        (planned.Source, planned.PaidMinutes, planned.Amount).ShouldBe(("Planned", 90, 450_000m));

        // Teachers cannot run payroll.
        (await teacherClient.GetAsync("/api/hr/payroll-periods", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var march = (await (await admin.PostAsJsonAsync("/api/hr/payroll-periods", new { from = "2027-03-01", to = "2027-03-31" }, Ct))
            .Content.ReadFromJsonAsync<PeriodView>(Ct))!;
        var payslip = march.Payslips.Single();
        (payslip.TeacherName, payslip.Sessions, payslip.PaidMinutes, payslip.Amount).ShouldBe(("GV Lương", 2, 170, 850_000m));
        (await admin.PostAsJsonAsync("/api/hr/payroll-periods", new { from = "2027-03-15", to = "2027-04-15" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await admin.PostAsync($"/api/hr/payroll-periods/{march.Id}/lock", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // A raise after locking never rewrites March; a March lesson reported late is paid in the next period.
        (await admin.PutAsJsonAsync($"/api/hr/teachers/{teacher}/rate", new { hourlyRate = 360_000m, currency = "VND", effectiveFrom = "2027-03-01" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.PostAsJsonAsync($"/api/workspace/dev/sessions/{online[1].Id}/simulate", new
        {
            participants = new[] { new { userId = (Guid?)teacher, displayName = "GV", joinedMinute = 0, leftMinute = 90 } },
            recording = false,
        }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await _api.DrainOutboxAsync();

        mine = (await teacherClient.GetFromJsonAsync<TimesheetView[]>("/api/hr/my/timesheets?from=2027-03-01&to=2027-03-31", Ct))!;
        mine.Where(t => t.Status == "Locked").Sum(t => t.Amount).ShouldBe(850_000m);
        var april = (await (await admin.PostAsJsonAsync("/api/hr/payroll-periods", new { from = "2027-04-01", to = "2027-04-30" }, Ct))
            .Content.ReadFromJsonAsync<PeriodView>(Ct))!;
        april.Payslips.Single().Amount.ShouldBe(540_000m);
    }

    private sealed record SessionView(Guid Id, string Status);

    private sealed record TimesheetView(Guid SessionId, string Source, int PaidMinutes, int LateMinutes, decimal Amount, string Status);

    private sealed record PayslipView(string TeacherName, int Sessions, int PaidMinutes, decimal Amount);

    private sealed record PeriodView(Guid Id, string Status, PayslipView[] Payslips);
}
