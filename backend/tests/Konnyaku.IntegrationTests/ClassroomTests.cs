using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Konnyaku.IntegrationTests;

/// <summary>
/// Built-in classroom (provider "Konnyaku"): rooms without a host pool, tickets behind the same permission check as
/// the join link, and the signaling service's reports turning into attendance and timesheets.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ClassroomTests(PostgresFixture db) : IAsyncDisposable
{
    private const string Password = "Admin-Pass-123";
    private readonly KonnyakuApiFactory _api = new(db);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _api.DisposeAsync();

    [Fact]
    public async Task Rooms_need_no_host_and_tickets_follow_the_join_rule()
    {
        var c = await CenterAsync("room");
        var session = (await c.SessionsAsync())[0];
        session.Status.ShouldBe("Provisioned", "no host account is needed for the built-in classroom");

        var join = await (await _api.ClientFor(c.Slug, c.LearnerToken).GetAsync($"/api/workspace/sessions/{session.Id}/join", Ct))
            .Content.ReadFromJsonAsync<JoinView>(Ct);
        join!.MeetingUri.ShouldMatch("^/room/[0-9a-f]{3}-[0-9a-f]{4}-[0-9a-f]{3}$");
        var code = join.MeetingUri["/room/".Length..];

        var ticketResponse = await _api.ClientFor(c.Slug, c.LearnerToken).PostAsync($"/api/workspace/rooms/{code}/ticket", null, Ct);
        ticketResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var ticket = (await ticketResponse.Content.ReadFromJsonAsync<TicketView>(Ct))!;
        (ticket.SessionId, ticket.Role, ticket.SignalingUrl).ShouldBe((session.Id, "learner", "/rtc/ws"));
        var claims = await ValidateTicketAsync(ticket.Ticket);
        (claims["sid"], claims["tid"], claims["sub"], claims["role"]).ShouldBe(
            (session.Id.ToString(), c.TenantId.ToString(), c.LearnerId.ToString(), "learner"));
        (ticket.ExpiresAt - DateTimeOffset.UtcNow).ShouldBeLessThan(TimeSpan.FromMinutes(3));

        // TURN REST credentials: coturn recomputes the password from the shared secret.
        var turn = ticket.IceServers.Single(s => s.Username is not null);
        turn.Credential.ShouldBe(Convert.ToBase64String(HMACSHA1.HashData(Encoding.UTF8.GetBytes("test-turn-secret"), Encoding.UTF8.GetBytes(turn.Username!))));
        long.Parse(turn.Username!.Split(':')[0]).ShouldBeGreaterThan(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        var teacher = await _api.ClientFor(c.Slug, c.TeacherToken).PostAsync($"/api/workspace/rooms/{code}/ticket", null, Ct);
        (await teacher.Content.ReadFromJsonAsync<TicketView>(Ct))!.Role.ShouldBe("host");

        // Another teacher, a learner of another class, a made-up code: no room.
        var otherTeacher = (await _api.StaffLoginAsync(c.Slug, c.OtherTeacherEmail, Password)).AccessToken;
        foreach (var (token, path) in new[]
                 {
                     (otherTeacher, $"/api/workspace/rooms/{code}/ticket"),
                     (c.OutsiderToken, $"/api/workspace/rooms/{code}/ticket"),
                     (c.LearnerToken, "/api/workspace/rooms/000-0000-000/ticket"),
                 })
        {
            var refused = await _api.ClientFor(c.Slug, token).PostAsync(path, null, Ct);
            refused.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await refused.Content.ReadFromJsonAsync<ErrorDto>(Ct))!.Code.ShouldBe("workspace.room_not_found");
        }
    }

    [Fact]
    public async Task Presence_reports_become_attendance_and_timesheet_when_the_room_ends()
    {
        var c = await CenterAsync("presence");
        var session = (await c.SessionsAsync())[0];
        var internalClient = _api.CreateClient();

        Task<HttpResponseMessage> Report(object body, string? token = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/workspace/internal/rooms/events") { Content = JsonContent.Create(body) };
            request.Headers.Add("X-Rtc-Token", token ?? ServiceToken(KonnyakuApiFactory.RtcSecret));
            return internalClient.SendAsync(request, Ct);
        }

        object Event(string kind, int minute, Guid? connection = null, Guid? user = null, string? name = null, string? role = null) => new
        {
            tenantId = c.TenantId, sessionId = session.Id, kind, at = session.StartsAt.AddMinutes(minute),
            connectionId = connection, userId = user, displayName = name, role,
        };

        var learnerFirst = Guid.NewGuid();
        var learnerAgain = Guid.NewGuid();
        var teacherConnection = Guid.NewGuid();
        var teacherAfterRestart = Guid.NewGuid();

        // Only the signaling service (shared secret) may report.
        (await Report(Event("joined", 0, teacherConnection, c.TeacherId, "GV", "host"), token: "forged")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await Report(Event("joined", 0, teacherConnection, c.TeacherId, "GV", "host"), ServiceToken("another-secret-0123456789abcdef-0123456789")))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // Teacher from the start; the learner drops after 30 minutes and is back 2 minutes later (reports may repeat).
        // At minute 40 the signaling service restarts: the teacher's "left" is lost, the reconnect is a new join.
        foreach (var report in new[]
                 {
                     Event("joined", 0, teacherConnection, c.TeacherId, "GV", "host"),
                     Event("joined", 5, learnerFirst, c.LearnerId, "HV", "learner"),
                     Event("joined", 5, learnerFirst, c.LearnerId, "HV", "learner"),
                     Event("left", 30, learnerFirst),
                     Event("joined", 32, learnerAgain, c.LearnerId, "HV", "learner"),
                     Event("joined", 40, teacherAfterRestart, c.TeacherId, "GV", "host"),
                     Event("ended", 90),
                     Event("ended", 95),
                     Event("left", 96, learnerAgain),
                 })
        {
            (await Report(report)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        await _api.DrainOutboxAsync();

        var drafts = (await _api.ClientFor(c.Slug, c.TeacherToken).GetFromJsonAsync<AttendanceView[]>($"/api/learning/sessions/{session.Id}/attendance", Ct))!;
        var learner = drafts.Single(a => a.StudentId == c.StudentId);
        (learner.Status, learner.PresentMinutes).ShouldBe(("Present", 83));
        drafts.Single(a => a.StudentId == c.OtherStudentId).Status.ShouldBe("Absent");

        var timesheet = (await _api.ClientFor(c.Slug, c.TeacherToken)
            .GetFromJsonAsync<TimesheetView[]>($"/api/hr/my/timesheets?from={session.LocalDate}&to={session.LocalDate}", Ct))!.Single();
        (timesheet.SessionId, timesheet.Source, timesheet.PaidMinutes).ShouldBe((session.Id, "Meeting", 90));

        // The room is over: no more tickets.
        var code = (await (await _api.ClientFor(c.Slug, c.LearnerToken).GetAsync($"/api/workspace/sessions/{session.Id}/join", Ct))
            .Content.ReadFromJsonAsync<JoinView>(Ct))!.MeetingUri["/room/".Length..];
        var after = await _api.ClientFor(c.Slug, c.LearnerToken).PostAsync($"/api/workspace/rooms/{code}/ticket", null, Ct);
        after.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await after.Content.ReadFromJsonAsync<ErrorDto>(Ct))!.Code.ShouldBe("workspace.room_ended");
    }

    [Fact]
    public async Task A_report_cannot_reach_another_tenant_than_the_host_it_is_sent_to()
    {
        var c = await CenterAsync("rtc-a");
        var other = await CenterAsync("rtc-b");
        var session = (await other.SessionsAsync())[0];
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/workspace/internal/rooms/events")
        {
            Content = JsonContent.Create(new { tenantId = other.TenantId, sessionId = session.Id, kind = "ended", at = session.StartsAt }),
        };
        request.Headers.Add("X-Rtc-Token", ServiceToken(KonnyakuApiFactory.RtcSecret));
        // Sent with tenant A's host header but naming tenant B.
        var client = _api.ClientFor(c.Slug);
        (await client.SendAsync(request, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private static string ServiceToken(string secret) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    {
        Issuer = "konnyaku-rtc",
        Audience = "konnyaku-api",
        Expires = DateTime.UtcNow.AddMinutes(1),
        SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)), SecurityAlgorithms.HmacSha256),
    });

    private static async Task<Dictionary<string, string>> ValidateTicketAsync(string ticket)
    {
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(ticket, new TokenValidationParameters
        {
            ValidIssuer = "konnyaku",
            ValidAudience = "konnyaku-rtc",
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(KonnyakuApiFactory.RtcSecret)),
        });
        result.IsValid.ShouldBeTrue(result.Exception?.Message);
        return result.Claims.ToDictionary(kv => kv.Key, kv => kv.Value switch
        {
            JsonElement e => e.ToString(),
            var v => v.ToString()!,
        });
    }

    private async Task<Center> CenterAsync(string prefix)
    {
        var email = $"admin-{Guid.NewGuid():N}@example.com";
        var tenant = await _api.CreateTenantWithAdminAsync(prefix, email, Password);
        var admin = _api.ClientFor(tenant.Slug, (await _api.StaffLoginAsync(tenant.Slug, email, Password)).AccessToken);

        async Task<Dictionary<string, JsonElement>> Post(string path, object body)
        {
            var response = await admin.PostAsJsonAsync(path, body, Ct);
            response.IsSuccessStatusCode.ShouldBeTrue($"{path}: {await response.Content.ReadAsStringAsync(Ct)}");
            return (await response.Content.ReadFromJsonAsync<Dictionary<string, JsonElement>>(Ct))!;
        }

        static Guid Id(Dictionary<string, JsonElement> json, string key = "id") => json[key].GetGuid();

        // A tenant may switch from the fake provider to the built-in classroom.
        (await admin.PutAsJsonAsync("/api/workspace/connection", new { provider = "Fake" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.PutAsJsonAsync("/api/workspace/connection", new { provider = "Konnyaku" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.PutAsJsonAsync("/api/workspace/connection", new { provider = "Zoom" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var teacherEmail = $"t-{Guid.NewGuid():N}@example.com";
        var otherTeacherEmail = $"o-{Guid.NewGuid():N}@example.com";
        var teacher = Id(await Post("/api/identity/staff", new { email = teacherEmail, displayName = "GV", initialPassword = Password, roles = new[] { "Teacher" } }), "userId");
        await Post("/api/identity/staff", new { email = otherTeacherEmail, displayName = "GV 2", initialPassword = Password, roles = new[] { "Teacher" } });
        var branch = Id(await Post("/api/organization/branches", new { code = "ONLINE", name = new { vi = "Online" }, timeZone = "Asia/Ho_Chi_Minh", isVirtual = true }));
        var program = Id(await Post("/api/academic/programs", new { code = "KR", name = new { vi = "KR" }, languageCode = "ko", frameworkCode = "TOPIK", isPublic = false }));
        var level = Id(await Post($"/api/academic/programs/{program}/levels", new { code = "T1", name = new { vi = "T1" }, targetLevelCode = "TOPIK1", sessionCount = 2, sessionMinutes = 90 }));

        async Task<Guid> ClassAsync(string code, string startTime)
        {
            var cls = Id(await Post("/api/academic/classes", new { code, courseLevelId = level, branchId = branch, deliveryMode = "Online", capacity = 6, startsOn = "2027-04-05" }));
            (await admin.PostAsJsonAsync($"/api/scheduling/classes/{cls}/schedule", new { daysOfWeek = new[] { 2, 4 }, startTime, teacherId = teacher }, Ct))
                .StatusCode.ShouldBe(HttpStatusCode.Created);
            return cls;
        }

        async Task<(Guid UserId, Guid StudentId, string Token)> LearnerAsync(Guid classId, string name)
        {
            var account = await Post("/api/identity/learners", new { displayName = name, role = "Student" });
            var student = Id(await Post("/api/crm/students", new { fullName = name, userId = account["userId"].GetString() }));
            await Post($"/api/academic/classes/{classId}/enrollments", new { studentId = student, type = "Regular" });
            var login = await _api.ClientFor(tenant.Slug).PostAsJsonAsync("/api/identity/learn/login",
                new { username = account["username"].GetString(), password = account["temporaryPassword"].GetString() }, Ct);
            return (account["userId"].GetGuid(), student, (await login.Content.ReadFromJsonAsync<SignInDto>(Ct))!.AccessToken);
        }

        var classId = await ClassAsync("KR-T1-01", "19:00");
        var otherClass = await ClassAsync("KR-T1-02", "08:00");
        var learner = await LearnerAsync(classId, "HV");
        var classmate = await LearnerAsync(classId, "HV 2");
        var outsider = await LearnerAsync(otherClass, "HV 3");
        await _api.DrainOutboxAsync();

        var teacherToken = (await _api.StaffLoginAsync(tenant.Slug, teacherEmail, Password)).AccessToken;
        return new Center(tenant.Id, tenant.Slug, admin, classId, teacher, teacherToken, otherTeacherEmail,
            learner.UserId, learner.StudentId, learner.Token, classmate.StudentId, outsider.Token, Ct);
    }

    private sealed record Center(
        Guid TenantId, string Slug, HttpClient Admin, Guid ClassId, Guid TeacherId, string TeacherToken, string OtherTeacherEmail,
        Guid LearnerId, Guid StudentId, string LearnerToken, Guid OtherStudentId, string OutsiderToken, CancellationToken Ct)
    {
        public async Task<SessionView[]> SessionsAsync() =>
            (await Admin.GetFromJsonAsync<SessionView[]>($"/api/scheduling/classes/{ClassId}/sessions", Ct))!;
    }

    private sealed record SessionView(Guid Id, string Status, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string LocalDate);

    private sealed record JoinView(string MeetingUri);

    private sealed record IceServer(string[] Urls, string? Username, string? Credential);

    private sealed record TicketView(Guid SessionId, string Ticket, DateTimeOffset ExpiresAt, string SignalingUrl, IceServer[] IceServers, string Role);

    private sealed record AttendanceView(Guid StudentId, string Status, int PresentMinutes);

    private sealed record TimesheetView(Guid SessionId, string Source, int PaidMinutes);
}
