using System.Net;
using System.Net.Http.Json;

namespace Konnyaku.IntegrationTests;

/// <summary>The whole lesson lifecycle across modules, driven only by public APIs and integration events.</summary>
[Collection(PostgresCollection.Name)]
public sealed class LearningFlowTests(PostgresFixture db) : IAsyncDisposable
{
    private const string Password = "Admin-Pass-123";
    private readonly KonnyakuApiFactory _api = new(db);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _api.DisposeAsync();

    [Fact]
    public async Task Meeting_log_becomes_confirmed_attendance_spent_lessons_and_recordings()
    {
        var email = $"admin-{Guid.NewGuid():N}@example.com";
        var tenant = await _api.CreateTenantWithAdminAsync("flow", email, Password);
        var admin = _api.ClientFor(tenant.Slug, (await _api.StaffLoginAsync(tenant.Slug, email, Password)).AccessToken);

        async Task<Dictionary<string, object>> Post(string path, object body)
        {
            var response = await admin.PostAsJsonAsync(path, body, Ct);
            response.IsSuccessStatusCode.ShouldBeTrue($"{path}: {await response.Content.ReadAsStringAsync(Ct)}");
            return (await response.Content.ReadFromJsonAsync<Dictionary<string, object>>(Ct))!;
        }

        static Guid Id(Dictionary<string, object> json, string key = "id") => Guid.Parse(json[key].ToString()!);

        (await admin.PutAsJsonAsync("/api/workspace/connection", new { provider = "Fake" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await Post("/api/workspace/hosts", new { email = "host@example.com" });
        var teacherEmail = $"t-{Guid.NewGuid():N}@example.com";
        var teacher = Id(await Post("/api/identity/staff", new { email = teacherEmail, displayName = "GV", initialPassword = Password, roles = new[] { "Teacher" } }), "userId");
        var branch = Id(await Post("/api/organization/branches", new { code = "ONLINE", name = new { vi = "Online" }, timeZone = "Asia/Ho_Chi_Minh", isVirtual = true }));
        var program = Id(await Post("/api/academic/programs", new { code = "JP", name = new { vi = "JP" }, languageCode = "ja", frameworkCode = "JLPT", isPublic = false }));
        var level = Id(await Post($"/api/academic/programs/{program}/levels", new { code = "N5", name = new { vi = "N5" }, targetLevelCode = "N5", sessionCount = 2, sessionMinutes = 90 }));
        var cls = Id(await Post("/api/academic/classes", new { code = "JP-N5-01", courseLevelId = level, branchId = branch, deliveryMode = "Online", capacity = 10, startsOn = "2027-03-01" }));
        var package = Id(await Post("/api/billing/packages", new { code = "N5-24", name = new { vi = "24 buổi" }, paidCredits = 24, bonusCredits = 0, price = 2_400_000m, currency = "VND" }));

        // Three learners buy the package for the class; the Worker enrolls them.
        var learners = new List<(Guid UserId, Guid StudentId, string Token)>();
        foreach (var name in new[] { "An", "Bình", "Chi" })
        {
            var account = await Post("/api/identity/learners", new { displayName = name, role = "Student" });
            var student = Id(await Post("/api/crm/students", new { fullName = name, userId = account["userId"].ToString() }));
            var order = Id(await Post("/api/billing/orders", new { studentId = student, packageId = package, classId = cls }));
            await Post($"/api/billing/orders/{order}/payments", new { amount = 2_400_000m, method = "cash" });
            var token = (await (await _api.ClientFor(tenant.Slug).PostAsJsonAsync("/api/identity/learn/login",
                new { username = account["username"].ToString(), password = account["temporaryPassword"].ToString() }, Ct))
                .Content.ReadFromJsonAsync<SignInDto>(Ct))!.AccessToken;
            learners.Add((Guid.Parse(account["userId"].ToString()!), student, token));
        }

        (await admin.PostAsJsonAsync($"/api/scheduling/classes/{cls}/schedule",
            new { daysOfWeek = new[] { 2, 4 }, startTime = "19:00", teacherId = teacher }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        await _api.DrainOutboxAsync();
        var session = (await admin.GetFromJsonAsync<SessionView[]>($"/api/scheduling/classes/{cls}/sessions", Ct))![0];
        session.Status.ShouldBe("Provisioned");

        // An on time, Bình 15 minutes late, Chi never joins; the teacher is there too.
        (await admin.PostAsJsonAsync($"/api/workspace/dev/sessions/{session.Id}/simulate", new
        {
            participants = new object[]
            {
                new { userId = learners[0].UserId, displayName = "An", joinedMinute = 0, leftMinute = 90 },
                new { userId = learners[1].UserId, displayName = "Bình", joinedMinute = 15, leftMinute = 90 },
                new { userId = teacher, displayName = "GV", joinedMinute = 0, leftMinute = 90 },
            },
            recording = true,
        }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await _api.DrainOutboxAsync();

        var teacherClient = _api.ClientFor(tenant.Slug, (await _api.StaffLoginAsync(tenant.Slug, teacherEmail, Password)).AccessToken);
        var drafts = (await teacherClient.GetFromJsonAsync<AttendanceView[]>($"/api/learning/sessions/{session.Id}/attendance", Ct))!;
        drafts.Single(a => a.StudentId == learners[0].StudentId).Status.ShouldBe("Present");
        drafts.Single(a => a.StudentId == learners[1].StudentId).Status.ShouldBe("Late");
        var chi = drafts.Single(a => a.StudentId == learners[2].StudentId);
        chi.Status.ShouldBe("Absent");

        // Chi's parent called in advance: the teacher excuses the absence, then confirms.
        var confirmed = await teacherClient.PostAsJsonAsync($"/api/learning/sessions/{session.Id}/attendance/confirm",
            new { overrides = new[] { new { enrollmentId = chi.EnrollmentId, status = "ExcusedAbsent" } } }, Ct);
        confirmed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await teacherClient.PostAsJsonAsync($"/api/learning/sessions/{session.Id}/attendance/confirm", new { overrides = Array.Empty<object>() }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await _api.DrainOutboxAsync();

        async Task<decimal> Remaining(int i) =>
            (await _api.ClientFor(tenant.Slug, learners[i].Token).GetFromJsonAsync<CreditView[]>("/api/billing/my/credits", Ct))!.Single().PaidCredits;
        (await Remaining(0)).ShouldBe(23m);
        (await Remaining(1)).ShouldBe(23m);
        (await Remaining(2)).ShouldBe(24m);

        // Every enrolled learner may replay the lesson — the absent one first of all.
        var replay = (await _api.ClientFor(tenant.Slug, learners[2].Token).GetFromJsonAsync<RecordingView[]>("/api/learning/my/recordings", Ct))!;
        replay.Single().SessionId.ShouldBe(session.Id);
    }

    private sealed record SessionView(Guid Id, string Status);

    private sealed record AttendanceView(Guid EnrollmentId, Guid StudentId, string Status, bool Confirmed);

    private sealed record CreditView(decimal PaidCredits);

    private sealed record RecordingView(Guid SessionId);
}
