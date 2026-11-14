using System.Net;
using System.Net.Http.Json;

namespace Konnyaku.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class MeetingEngineTests(PostgresFixture db) : IAsyncDisposable
{
    private const string Password = "Admin-Pass-123";
    private readonly KonnyakuApiFactory _api = new(db);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _api.DisposeAsync();

    [Fact]
    public async Task Pool_never_gives_one_host_to_overlapping_sessions()
    {
        var c = await CenterAsync("pool", hosts: 1);
        var classA = await c.ClassAsync("POOL-A", c.TeacherA);
        var classB = await c.ClassAsync("POOL-B", c.TeacherB);
        await _api.DrainOutboxAsync();

        var a = await c.SessionsAsync(classA);
        var b = await c.SessionsAsync(classB);
        a.ShouldAllBe(s => s.Status == "Provisioned");
        b.ShouldAllBe(s => s.Status == "Scheduled");

        // A second licence in the pool lets the remaining sessions be provisioned once re-planned.
        (await c.Admin.PostAsJsonAsync("/api/workspace/hosts", new { email = "host2@example.com" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        foreach (var session in b)
        {
            (await c.Admin.PostAsJsonAsync($"/api/scheduling/sessions/{session.Id}/reschedule",
                new { date = session.LocalDate, startTime = "19:00" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await _api.DrainOutboxAsync();
        (await c.SessionsAsync(classB)).ShouldAllBe(s => s.Status == "Provisioned");
    }

    [Fact]
    public async Task Only_the_teacher_and_enrolled_learners_get_the_meeting_link()
    {
        var c = await CenterAsync("join", hosts: 2);
        var classA = await c.ClassAsync("JOIN-A", c.TeacherA);
        var classB = await c.ClassAsync("JOIN-B", c.TeacherB);
        var learnerA = await c.LearnerInAsync(classA, _api);
        await _api.DrainOutboxAsync();

        var session = (await c.SessionsAsync(classA))[0];
        var joinA = await _api.ClientFor(c.Slug, learnerA).GetAsync($"/api/workspace/sessions/{session.Id}/join", Ct);
        joinA.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await joinA.Content.ReadFromJsonAsync<JoinView>(Ct))!.MeetingUri.ShouldStartWith("/dev-meeting/");

        var uris = new HashSet<string>();
        foreach (var s in await c.SessionsAsync(classA))
        {
            var join = await (await _api.ClientFor(c.Slug, learnerA).GetAsync($"/api/workspace/sessions/{s.Id}/join", Ct)).Content.ReadFromJsonAsync<JoinView>(Ct);
            uris.Add(join!.MeetingUri);
        }

        uris.Count.ShouldBe(2, "each session gets its own room");

        var learnerB = await c.LearnerInAsync(classB, _api);
        (await _api.ClientFor(c.Slug, learnerB).GetAsync($"/api/workspace/sessions/{session.Id}/join", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var teacherA = (await _api.StaffLoginAsync(c.Slug, c.TeacherAEmail, Password)).AccessToken;
        var teacherB = (await _api.StaffLoginAsync(c.Slug, c.TeacherBEmail, Password)).AccessToken;
        (await _api.ClientFor(c.Slug, teacherA).GetAsync($"/api/workspace/sessions/{session.Id}/join", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _api.ClientFor(c.Slug, teacherB).GetAsync($"/api/workspace/sessions/{session.Id}/join", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Cancelling_a_session_frees_its_host_and_simulation_ends_the_meeting()
    {
        var c = await CenterAsync("free", hosts: 1);
        var classA = await c.ClassAsync("FREE-A", c.TeacherA);
        await _api.DrainOutboxAsync();
        var sessions = await c.SessionsAsync(classA);

        (await c.Admin.PostAsJsonAsync($"/api/scheduling/sessions/{sessions[1].Id}/cancel", new { reason = "holiday" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _api.DrainOutboxAsync();
        var leases = (await c.Admin.GetFromJsonAsync<LeaseView[]>("/api/workspace/leases?from=2026-01-01T00:00:00Z&to=2028-01-01T00:00:00Z", Ct))!;
        leases.Single(l => l.SessionId == sessions[1].Id).Status.ShouldBe("Cancelled");

        var simulated = await c.Admin.PostAsJsonAsync($"/api/workspace/dev/sessions/{sessions[0].Id}/simulate", new
        {
            participants = new[] { new { userId = (Guid?)c.TeacherA, displayName = "GV", joinedMinute = 0, leftMinute = 90 } },
            recording = true,
        }, Ct);
        simulated.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        leases = (await c.Admin.GetFromJsonAsync<LeaseView[]>("/api/workspace/leases?from=2026-01-01T00:00:00Z&to=2028-01-01T00:00:00Z", Ct))!;
        leases.Single(l => l.SessionId == sessions[0].Id).Status.ShouldBe("Released");
    }

    private async Task<Center> CenterAsync(string prefix, int hosts)
    {
        var email = $"admin-{Guid.NewGuid():N}@example.com";
        var tenant = await _api.CreateTenantWithAdminAsync(prefix, email, Password);
        var admin = _api.ClientFor(tenant.Slug, (await _api.StaffLoginAsync(tenant.Slug, email, Password)).AccessToken);

        async Task<Guid> Post(string path, object body)
        {
            var response = await admin.PostAsJsonAsync(path, body, Ct);
            response.IsSuccessStatusCode.ShouldBeTrue($"{path}: {await response.Content.ReadAsStringAsync(Ct)}");
            var json = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>(Ct);
            return Guid.Parse((json!.TryGetValue("id", out var id) ? id : json["userId"]).ToString()!);
        }

        (await admin.PutAsJsonAsync("/api/workspace/connection", new { provider = "Fake" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        for (var i = 0; i < hosts; i++)
        {
            await Post("/api/workspace/hosts", new { email = $"host{i}@example.com" });
        }

        var teacherAEmail = $"ta-{Guid.NewGuid():N}@example.com";
        var teacherBEmail = $"tb-{Guid.NewGuid():N}@example.com";
        var teacherA = await Post("/api/identity/staff", new { email = teacherAEmail, displayName = "GV A", initialPassword = Password, roles = new[] { "Teacher" } });
        var teacherB = await Post("/api/identity/staff", new { email = teacherBEmail, displayName = "GV B", initialPassword = Password, roles = new[] { "Teacher" } });
        var branch = await Post("/api/organization/branches", new { code = "ONLINE", name = new { vi = "Online" }, timeZone = "Asia/Ho_Chi_Minh", isVirtual = true });
        var program = await Post("/api/academic/programs", new { code = "EN", name = new { vi = "EN" }, languageCode = "en", frameworkCode = "CEFR", isPublic = false });
        var level = await Post($"/api/academic/programs/{program}/levels", new { code = "A1", name = new { vi = "A1" }, targetLevelCode = "A1", sessionCount = 2, sessionMinutes = 90 });
        return new Center(tenant.Slug, admin, branch, level, teacherA, teacherB, teacherAEmail, teacherBEmail, Ct);
    }

    private sealed record Center(string Slug, HttpClient Admin, Guid BranchId, Guid LevelId, Guid TeacherA, Guid TeacherB,
        string TeacherAEmail, string TeacherBEmail, CancellationToken Ct)
    {
        public async Task<Guid> ClassAsync(string code, Guid teacherId)
        {
            var cls = await (await Admin.PostAsJsonAsync("/api/academic/classes", new
            {
                code, courseLevelId = LevelId, branchId = BranchId, deliveryMode = "Online", capacity = 10, startsOn = "2027-02-01",
            }, Ct)).Content.ReadFromJsonAsync<IdView>(Ct);
            (await Admin.PostAsJsonAsync($"/api/scheduling/classes/{cls!.Id}/schedule",
                new { daysOfWeek = new[] { 2, 4 }, startTime = "19:00", teacherId }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
            return cls.Id;
        }

        public async Task<SessionView[]> SessionsAsync(Guid classId) =>
            (await Admin.GetFromJsonAsync<SessionView[]>($"/api/scheduling/classes/{classId}/sessions", Ct))!;

        public async Task<string> LearnerInAsync(Guid classId, KonnyakuApiFactory api)
        {
            var account = (await (await Admin.PostAsJsonAsync("/api/identity/learners", new { displayName = "HV", role = "Student" }, Ct))
                .Content.ReadFromJsonAsync<LearnerAccount>(Ct))!;
            var student = (await (await Admin.PostAsJsonAsync("/api/crm/students", new { fullName = "HV", userId = account.UserId }, Ct))
                .Content.ReadFromJsonAsync<IdView>(Ct))!;
            await Admin.PostAsJsonAsync($"/api/academic/classes/{classId}/enrollments", new { studentId = student.Id, type = "Regular" }, Ct);
            return (await (await api.ClientFor(Slug).PostAsJsonAsync("/api/identity/learn/login",
                new { username = account.Username, password = account.TemporaryPassword }, Ct)).Content.ReadFromJsonAsync<SignInDto>(Ct))!.AccessToken;
        }
    }

    private sealed record IdView(Guid Id);

    private sealed record SessionView(Guid Id, string LocalDate, string Status);

    private sealed record LeaseView(Guid SessionId, string Status);

    private sealed record JoinView(string MeetingUri);

    private sealed record LearnerAccount(Guid UserId, string Username, string TemporaryPassword);
}
