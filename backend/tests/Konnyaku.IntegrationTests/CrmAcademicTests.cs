using System.Net;
using System.Net.Http.Json;

namespace Konnyaku.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class CrmAcademicTests(PostgresFixture db) : IAsyncDisposable
{
    private const string Password = "Admin-Pass-123";
    private readonly KonnyakuApiFactory _api = new(db);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _api.DisposeAsync();

    [Fact]
    public async Task Public_registration_creates_one_lead_assigned_to_a_consultant()
    {
        var (tenant, admin) = await TenantAsync("crm");
        var consultant = await (await admin.PostAsJsonAsync("/api/identity/staff", new
        {
            email = $"consultant-{tenant.Slug}@example.com", displayName = "Tư vấn Test", initialPassword = Password, roles = new[] { "Consultant" },
        }, Ct)).Content.ReadFromJsonAsync<UserIdDto>(Ct);

        var visitor = _api.ClientFor(tenant.Slug);
        var form = Lead("0901234567");
        (await visitor.PostAsJsonAsync("/api/crm/public/leads", form, Ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await visitor.PostAsJsonAsync("/api/crm/public/leads", form with { Phone = "+84 901 234 567", Goal = "JLPT N4" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await visitor.PostAsJsonAsync("/api/crm/public/leads", Lead("0909999999") with { Website = "http://spam" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var leads = (await admin.GetFromJsonAsync<LeadView[]>("/api/crm/leads", Ct))!;
        leads.Length.ShouldBe(1);
        leads[0].Goal.ShouldBe("JLPT N4");
        leads[0].AssignedConsultantId.ShouldBe(consultant!.UserId);
        leads[0].FollowUpDueAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Registration_validates_minors_and_consent()
    {
        var (tenant, _) = await TenantAsync("crmv");
        var visitor = _api.ClientFor(tenant.Slug);

        var minor = await visitor.PostAsJsonAsync("/api/crm/public/leads", Lead("0911111111") with { BirthYear = DateTime.UtcNow.Year - 10 }, Ct);
        (await minor.Content.ReadFromJsonAsync<ErrorDto>(Ct))!.Code.ShouldBe("crm.guardian_required");

        var noConsent = await visitor.PostAsJsonAsync("/api/crm/public/leads", Lead("0922222222") with { ConsentGiven = false }, Ct);
        (await noConsent.Content.ReadFromJsonAsync<ErrorDto>(Ct))!.Code.ShouldBe("crm.consent_required");
    }

    [Fact]
    public async Task Programs_must_use_a_framework_of_their_language()
    {
        var (_, admin) = await TenantAsync("acad");
        var japanese = await admin.PostAsJsonAsync("/api/academic/programs", Program("JP-COMM", "ja", "JLPT"), Ct);
        japanese.StatusCode.ShouldBe(HttpStatusCode.Created);
        var wrong = await admin.PostAsJsonAsync("/api/academic/programs", Program("EN-JLPT", "en", "JLPT"), Ct);
        (await wrong.Content.ReadFromJsonAsync<ErrorDto>(Ct))!.Code.ShouldBe("academic.framework_not_for_language");
        (await admin.PostAsJsonAsync("/api/academic/programs", Program("FR-A1", "fr", "CEFR"), Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);

        var program = (await japanese.Content.ReadFromJsonAsync<IdDto>(Ct))!;
        var level = await admin.PostAsJsonAsync($"/api/academic/programs/{program.Id}/levels",
            new { code = "N5-A", name = new { vi = "Sơ cấp 1" }, targetLevelCode = "N5", sessionCount = 24, sessionMinutes = 90 }, Ct);
        level.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await level.Content.ReadFromJsonAsync<LevelView>(Ct))!.SessionCount.ShouldBe(24);
        var badLevel = await admin.PostAsJsonAsync($"/api/academic/programs/{program.Id}/levels",
            new { code = "X", name = new { vi = "X" }, targetLevelCode = "B2", sessionCount = 10, sessionMinutes = 90 }, Ct);
        (await badLevel.Content.ReadFromJsonAsync<ErrorDto>(Ct))!.Code.ShouldBe("academic.level_not_in_framework");

        var publicPrograms = (await _api.ClientFor("unused").GetFromJsonAsync<IdDto[]>("/api/academic/public/programs", Ct))!;
        publicPrograms.ShouldBeEmpty();
    }

    [Fact]
    public async Task Concurrent_enrollments_never_exceed_class_capacity()
    {
        var (_, admin) = await TenantAsync("cap");
        var classId = await CreateClassAsync(admin, capacity: 2);
        var students = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            var student = await (await admin.PostAsJsonAsync("/api/crm/students", new { fullName = $"Học viên {i}" }, Ct))
                .Content.ReadFromJsonAsync<IdDto>(Ct);
            students.Add(student!.Id);
        }

        var responses = await Task.WhenAll(students.Select(s =>
            admin.PostAsJsonAsync($"/api/academic/classes/{classId}/enrollments", new { studentId = s, type = "Regular" }, Ct)));

        responses.Count(r => r.StatusCode == HttpStatusCode.Created).ShouldBe(2);
        responses.Count(r => r.StatusCode == HttpStatusCode.Conflict).ShouldBe(4);
        (await admin.GetFromJsonAsync<object[]>($"/api/academic/classes/{classId}/roster", Ct))!.Length.ShouldBe(2);
    }

    [Fact]
    public async Task Dropping_an_enrollment_frees_the_seat()
    {
        var (_, admin) = await TenantAsync("drop");
        var classId = await CreateClassAsync(admin, capacity: 1);
        var first = (await (await admin.PostAsJsonAsync("/api/crm/students", new { fullName = "A" }, Ct)).Content.ReadFromJsonAsync<IdDto>(Ct))!;
        var second = (await (await admin.PostAsJsonAsync("/api/crm/students", new { fullName = "B" }, Ct)).Content.ReadFromJsonAsync<IdDto>(Ct))!;

        var enrolled = (await (await admin.PostAsJsonAsync($"/api/academic/classes/{classId}/enrollments",
            new { studentId = first.Id, type = "Regular" }, Ct)).Content.ReadFromJsonAsync<RosterEntry>(Ct))!;
        (await admin.PostAsJsonAsync($"/api/academic/classes/{classId}/enrollments", new { studentId = second.Id, type = "Regular" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);

        (await admin.PostAsync($"/api/academic/enrollments/{enrolled.EnrollmentId}/drop", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.PostAsJsonAsync($"/api/academic/classes/{classId}/enrollments", new { studentId = second.Id, type = "Regular" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    private async Task<Guid> CreateClassAsync(HttpClient admin, int capacity)
    {
        var program = (await (await admin.PostAsJsonAsync("/api/academic/programs", Program($"P{Guid.NewGuid():N}"[..10], "en", "CEFR"), Ct))
            .Content.ReadFromJsonAsync<IdDto>(Ct))!;
        var level = (await (await admin.PostAsJsonAsync($"/api/academic/programs/{program.Id}/levels",
            new { code = "A1", name = new { vi = "A1" }, targetLevelCode = "A1", sessionCount = 12, sessionMinutes = 60 }, Ct))
            .Content.ReadFromJsonAsync<IdDto>(Ct))!;
        var branch = (await (await admin.PostAsJsonAsync("/api/organization/branches",
            new { code = "ONLINE", name = new { vi = "Online" }, timeZone = "Asia/Ho_Chi_Minh", isVirtual = true }, Ct))
            .Content.ReadFromJsonAsync<IdDto>(Ct))!;
        var created = await admin.PostAsJsonAsync("/api/academic/classes", new
        {
            code = $"C{Guid.NewGuid():N}"[..10], courseLevelId = level.Id, branchId = branch.Id, deliveryMode = "Online",
            capacity, startsOn = "2027-01-04",
        }, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var cls = (await created.Content.ReadFromJsonAsync<ClassView>(Ct))!;
        cls.TimeZone.ShouldBe("Asia/Ho_Chi_Minh");
        return cls.Id;
    }

    private async Task<(TenantDto Tenant, HttpClient Admin)> TenantAsync(string prefix)
    {
        var email = $"admin-{Guid.NewGuid():N}@example.com";
        var tenant = await _api.CreateTenantWithAdminAsync(prefix, email, Password);
        var token = (await _api.StaffLoginAsync(tenant.Slug, email, Password)).AccessToken;
        return (tenant, _api.ClientFor(tenant.Slug, token));
    }

    private static LeadForm Lead(string phone) => new("Nguyễn Test", 1998, null, phone, null, "ja", "Giao tiếp", "v1", true, "vi", null);

    private static object Program(string code, string language, string framework) => new
    {
        code, name = new { vi = code, en = code }, languageCode = language, frameworkCode = framework, isPublic = false,
    };

    private sealed record LeadForm(string StudentName, int? BirthYear, string? GuardianName, string? Phone, string? Email,
        string LanguageCode, string? Goal, string ConsentVersion, bool ConsentGiven, string Locale, string? Website);

    private sealed record LeadView(Guid Id, string? Goal, Guid? AssignedConsultantId, DateTimeOffset? FollowUpDueAt);

    private sealed record UserIdDto(Guid UserId);

    private sealed record IdDto(Guid Id);

    private sealed record LevelView(Guid Id, int SessionCount);

    private sealed record ClassView(Guid Id, string TimeZone);

    private sealed record RosterEntry(Guid EnrollmentId);
}
