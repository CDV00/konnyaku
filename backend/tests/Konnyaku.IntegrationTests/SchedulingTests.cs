using System.Net;
using System.Net.Http.Json;

namespace Konnyaku.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class SchedulingTests(PostgresFixture db) : IAsyncDisposable
{
    private const string Password = "Admin-Pass-123";
    private readonly KonnyakuApiFactory _api = new(db);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _api.DisposeAsync();

    [Fact]
    public async Task A_teacher_cannot_be_booked_twice_at_the_same_time()
    {
        var center = await CenterAsync("sch");
        var classA = await center.CreateClassAsync("CLS-A");
        var classB = await center.CreateClassAsync("CLS-B");

        var scheduled = await center.Admin.PostAsJsonAsync($"/api/scheduling/classes/{classA}/schedule",
            new { daysOfWeek = new[] { 2, 4 }, startTime = "19:00", teacherId = center.TeacherId }, Ct);
        scheduled.StatusCode.ShouldBe(HttpStatusCode.Created);
        var sessionsA = (await scheduled.Content.ReadFromJsonAsync<SessionView[]>(Ct))!;
        sessionsA.Length.ShouldBe(4);
        sessionsA[0].StartsAt.UtcDateTime.Hour.ShouldBe(12);

        var clash = await center.Admin.PostAsJsonAsync($"/api/scheduling/classes/{classB}/schedule",
            new { daysOfWeek = new[] { 2, 4 }, startTime = "19:30", teacherId = center.TeacherId }, Ct);
        (await clash.Content.ReadFromJsonAsync<ErrorDto>(Ct))!.Code.ShouldBe("scheduling.resource_conflict");
        (await center.Admin.GetFromJsonAsync<SessionView[]>($"/api/scheduling/classes/{classB}/sessions", Ct))!.ShouldBeEmpty();

        var earlier = await center.Admin.PostAsJsonAsync($"/api/scheduling/classes/{classB}/schedule",
            new { daysOfWeek = new[] { 2, 4 }, startTime = "17:00", teacherId = center.TeacherId }, Ct);
        earlier.StatusCode.ShouldBe(HttpStatusCode.Created);
        var sessionsB = (await earlier.Content.ReadFromJsonAsync<SessionView[]>(Ct))!;

        // Moving B's first session onto A's first session clashes; after A's is cancelled, it fits.
        var target = new { date = sessionsA[0].LocalDate, startTime = "19:00" };
        var move = await center.Admin.PostAsJsonAsync($"/api/scheduling/sessions/{sessionsB[0].Id}/reschedule", target, Ct);
        (await move.Content.ReadFromJsonAsync<ErrorDto>(Ct))!.Code.ShouldBe("scheduling.resource_conflict");

        (await center.Admin.PostAsJsonAsync($"/api/scheduling/sessions/{sessionsA[0].Id}/cancel", new { reason = "teacher_sick" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await center.Admin.PostAsJsonAsync($"/api/scheduling/sessions/{sessionsB[0].Id}/reschedule", target, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Concurrent_moves_into_the_same_slot_let_exactly_one_win()
    {
        var center = await CenterAsync("race");
        var classA = await center.CreateClassAsync("CLS-A");
        var classB = await center.CreateClassAsync("CLS-B");
        var a = (await (await center.Admin.PostAsJsonAsync($"/api/scheduling/classes/{classA}/schedule",
            new { daysOfWeek = new[] { 1 }, startTime = "08:00", teacherId = center.TeacherId }, Ct)).Content.ReadFromJsonAsync<SessionView[]>(Ct))!;
        var b = (await (await center.Admin.PostAsJsonAsync($"/api/scheduling/classes/{classB}/schedule",
            new { daysOfWeek = new[] { 3 }, startTime = "08:00", teacherId = center.TeacherId }, Ct)).Content.ReadFromJsonAsync<SessionView[]>(Ct))!;

        var slot = new { date = "2027-06-05", startTime = "10:00" };
        var results = await Task.WhenAll(
            center.Admin.PostAsJsonAsync($"/api/scheduling/sessions/{a[1].Id}/reschedule", slot, Ct),
            center.Admin.PostAsJsonAsync($"/api/scheduling/sessions/{b[1].Id}/reschedule", slot, Ct));

        results.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        results.Count(r => r.StatusCode == HttpStatusCode.Conflict).ShouldBe(1);
    }

    [Fact]
    public async Task Learner_and_teacher_see_their_own_schedules()
    {
        var center = await CenterAsync("mine");
        var classId = await center.CreateClassAsync("CLS-A");
        (await center.Admin.PostAsJsonAsync($"/api/scheduling/classes/{classId}/schedule",
            new { daysOfWeek = new[] { 6 }, startTime = "09:00", effectiveFrom = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)), teacherId = center.TeacherId }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Created);

        var account = (await (await center.Admin.PostAsJsonAsync("/api/identity/learners", new { displayName = "HV", role = "Student" }, Ct))
            .Content.ReadFromJsonAsync<LearnerAccount>(Ct))!;
        var student = (await (await center.Admin.PostAsJsonAsync("/api/crm/students", new { fullName = "HV", userId = account.UserId }, Ct))
            .Content.ReadFromJsonAsync<IdView>(Ct))!;
        (await center.Admin.PostAsJsonAsync($"/api/academic/classes/{classId}/enrollments", new { studentId = student.Id, type = "Regular" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Created);

        var learnerToken = (await (await _api.ClientFor(center.Slug).PostAsJsonAsync("/api/identity/learn/login",
            new { username = account.Username, password = account.TemporaryPassword }, Ct)).Content.ReadFromJsonAsync<SignInDto>(Ct))!.AccessToken;
        var mine = (await _api.ClientFor(center.Slug, learnerToken).GetFromJsonAsync<SessionView[]>("/api/scheduling/my/sessions", Ct))!;
        mine.Length.ShouldBeGreaterThan(0);
        mine.ShouldAllBe(s => s.ClassId == classId);

        var teacherToken = (await _api.StaffLoginAsync(center.Slug, center.TeacherEmail, Password)).AccessToken;
        var teaching = (await _api.ClientFor(center.Slug, teacherToken).GetFromJsonAsync<SessionView[]>("/api/scheduling/my/teaching", Ct))!;
        teaching.Length.ShouldBe(mine.Length);
    }

    private async Task<Center> CenterAsync(string prefix)
    {
        var email = $"admin-{Guid.NewGuid():N}@example.com";
        var tenant = await _api.CreateTenantWithAdminAsync(prefix, email, Password);
        var admin = _api.ClientFor(tenant.Slug, (await _api.StaffLoginAsync(tenant.Slug, email, Password)).AccessToken);
        var teacherEmail = $"teacher-{Guid.NewGuid():N}@example.com";
        var teacher = (await (await admin.PostAsJsonAsync("/api/identity/staff",
            new { email = teacherEmail, displayName = "GV Test", initialPassword = Password, roles = new[] { "Teacher" } }, Ct))
            .Content.ReadFromJsonAsync<UserIdView>(Ct))!;
        var branch = (await (await admin.PostAsJsonAsync("/api/organization/branches",
            new { code = "ONLINE", name = new { vi = "Online" }, timeZone = "Asia/Ho_Chi_Minh", isVirtual = true }, Ct))
            .Content.ReadFromJsonAsync<IdView>(Ct))!;
        var program = (await (await admin.PostAsJsonAsync("/api/academic/programs",
            new { code = "EN", name = new { vi = "EN" }, languageCode = "en", frameworkCode = "CEFR", isPublic = false }, Ct))
            .Content.ReadFromJsonAsync<IdView>(Ct))!;
        var level = (await (await admin.PostAsJsonAsync($"/api/academic/programs/{program.Id}/levels",
            new { code = "A1", name = new { vi = "A1" }, targetLevelCode = "A1", sessionCount = 4, sessionMinutes = 90 }, Ct))
            .Content.ReadFromJsonAsync<IdView>(Ct))!;
        return new Center(tenant.Slug, admin, teacher.UserId, teacherEmail, branch.Id, level.Id, Ct);
    }

    private sealed record Center(string Slug, HttpClient Admin, Guid TeacherId, string TeacherEmail, Guid BranchId, Guid LevelId, CancellationToken Ct)
    {
        public async Task<Guid> CreateClassAsync(string code)
        {
            var response = await Admin.PostAsJsonAsync("/api/academic/classes", new
            {
                code, courseLevelId = LevelId, branchId = BranchId, deliveryMode = "Online", capacity = 10, startsOn = "2027-01-04",
            }, Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
            return (await response.Content.ReadFromJsonAsync<IdView>(Ct))!.Id;
        }
    }

    private sealed record SessionView(Guid Id, Guid ClassId, DateTimeOffset StartsAt, string LocalDate, string Status);

    private sealed record IdView(Guid Id);

    private sealed record UserIdView(Guid UserId);

    private sealed record LearnerAccount(Guid UserId, string Username, string TemporaryPassword);
}
