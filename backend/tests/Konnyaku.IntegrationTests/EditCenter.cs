using System.Net;
using System.Net.Http.Json;

namespace Konnyaku.IntegrationTests;

/// <summary>
/// A center with two branches (A, B), a program with one level, a teacher and an admin — the common ground of the
/// edit, permission and history tests.
/// </summary>
internal sealed class EditCenter
{
    public const string Password = "Admin-Pass-123";

    private readonly KonnyakuApiFactory _api;

    private EditCenter(KonnyakuApiFactory api, string slug, HttpClient admin, Guid adminId)
    {
        _api = api;
        Slug = slug;
        Admin = admin;
        AdminId = adminId;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public string Slug { get; }
    public HttpClient Admin { get; }
    public Guid AdminId { get; }
    public Guid BranchA { get; private set; }
    public Guid BranchB { get; private set; }
    public Guid ProgramId { get; private set; }
    public Guid LevelId { get; private set; }
    public Guid TeacherId { get; private set; }
    public Guid OtherTeacherId { get; private set; }

    public static async Task<EditCenter> CreateAsync(KonnyakuApiFactory api, string prefix)
    {
        var email = $"admin-{Guid.NewGuid():N}@example.com";
        var tenant = await api.CreateTenantWithAdminAsync(prefix, email, Password);
        var login = await api.StaffLoginAsync(tenant.Slug, email, Password);
        var admin = api.ClientFor(tenant.Slug, login.AccessToken);
        var me = (await admin.GetFromJsonAsync<MeView>("/api/identity/me", Ct))!;
        var center = new EditCenter(api, tenant.Slug, admin, me.UserId);
        center.BranchA = await center.PostIdAsync("/api/organization/branches", new { code = "CS-A", name = new { vi = "Cơ sở A" }, timeZone = "Asia/Ho_Chi_Minh", isVirtual = false });
        center.BranchB = await center.PostIdAsync("/api/organization/branches", new { code = "CS-B", name = new { vi = "Cơ sở B" }, timeZone = "Asia/Ho_Chi_Minh", isVirtual = false });
        center.ProgramId = await center.PostIdAsync("/api/academic/programs", new { code = "EN", name = new { vi = "Tiếng Anh" }, languageCode = "en", frameworkCode = "CEFR", isPublic = true });
        center.LevelId = await center.PostIdAsync($"/api/academic/programs/{center.ProgramId}/levels",
            new { code = "A1", name = new { vi = "A1" }, targetLevelCode = "A1", sessionCount = 4, sessionMinutes = 90 });
        center.TeacherId = (await center.AddStaffAsync("Teacher")).UserId;
        center.OtherTeacherId = (await center.AddStaffAsync("Teacher")).UserId;
        return center;
    }

    /// <summary>Adds a staff member (roles limited to <paramref name="branchIds"/> when given) and signs them in.</summary>
    public async Task<(Guid UserId, HttpClient Client)> AddStaffAsync(string role, params Guid[] branchIds)
    {
        var email = $"{role.ToLowerInvariant()}-{Guid.NewGuid():N}@example.com";
        var response = await Admin.PostAsJsonAsync("/api/identity/staff",
            new { email, displayName = $"{role} Test", initialPassword = Password, roles = new[] { role }, branchIds }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var userId = (await response.Content.ReadFromJsonAsync<UserIdView>(Ct))!.UserId;
        var token = (await _api.StaffLoginAsync(Slug, email, Password)).AccessToken;
        return (userId, _api.ClientFor(Slug, token));
    }

    public Task<Guid> CreateClassAsync(string code, Guid branchId, string mode = "Online", int capacity = 10) =>
        PostIdAsync("/api/academic/classes", new { code, courseLevelId = LevelId, branchId, deliveryMode = mode, capacity, startsOn = "2027-01-04" });

    public async Task<SessionView[]> ScheduleAsync(Guid classId, int[] days, string startTime, Guid? teacherId)
    {
        var response = await Admin.PostAsJsonAsync($"/api/scheduling/classes/{classId}/schedule", new { daysOfWeek = days, startTime, teacherId }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<SessionView[]>(Ct))!;
    }

    public async Task<Guid> EnrollNewStudentAsync(Guid classId, string name = "Học viên")
    {
        var studentId = await PostIdAsync("/api/crm/students", new { fullName = name });
        (await Admin.PostAsJsonAsync($"/api/academic/classes/{classId}/enrollments", new { studentId, type = "Regular" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        return studentId;
    }

    public async Task<ClassView> ClassAsync(Guid classId) =>
        (await Admin.GetFromJsonAsync<ClassView[]>("/api/academic/classes", Ct))!.Single(c => c.Id == classId);

    public async Task<SessionView[]> SessionsAsync(Guid classId) =>
        (await Admin.GetFromJsonAsync<SessionView[]>($"/api/scheduling/classes/{classId}/sessions", Ct))!;

    public async Task<HistoryEntry[]> HistoryAsync(string module, Guid id) =>
        (await Admin.GetFromJsonAsync<HistoryEntry[]>($"/api/{module}/history/{id}", Ct))!;

    public async Task<Guid> PostIdAsync(string path, object body)
    {
        var response = await Admin.PostAsJsonAsync(path, body, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<IdView>(Ct))!.Id;
    }
}

internal static class ResponseAssertions
{
    public static async Task ShouldFailWith(this HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(status, body);
        body.ShouldContain($"\"{code}\"");
    }

    public static async Task ShouldSucceed(this HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        response.IsSuccessStatusCode.ShouldBeTrue($"{(int)response.StatusCode}: {body}");
    }
}

internal sealed record MeView(Guid UserId);

internal sealed record IdView(Guid Id);

internal sealed record UserIdView(Guid UserId);

internal sealed record ClassView(Guid Id, string Code, Guid CourseLevelId, Guid BranchId, string DeliveryMode, int Capacity, int EnrolledCount,
    string StartsOn, string Status);

internal sealed record SessionView(Guid Id, Guid ClassId, int SequenceNo, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string LocalDate,
    string DeliveryMode, Guid? TeacherId, Guid? RoomId, string Status);

internal sealed record FieldChangeView(string Field, string? Old, string? New);

internal sealed record HistoryEntry(Guid Id, string EntityType, Guid EntityId, string Action, Guid? ActorId, string? ActorName,
    DateTimeOffset OccurredAt, FieldChangeView[] Changes);
