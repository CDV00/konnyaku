using System.Net;
using System.Net.Http.Json;

namespace Konnyaku.IntegrationTests;

/// <summary>Lists the ops admin screens read: staff, packages and pay rates.</summary>
[Collection(PostgresCollection.Name)]
public sealed class AdminListTests(PostgresFixture db) : IAsyncDisposable
{
    private const string Password = "Admin-Pass-123";
    private readonly KonnyakuApiFactory _api = new(db);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _api.DisposeAsync();

    [Fact]
    public async Task Admin_lists_staff_packages_and_rates_of_its_own_tenant_only()
    {
        var email = $"admin-{Guid.NewGuid():N}@example.com";
        var tenant = await _api.CreateTenantWithAdminAsync("adm", email, Password);
        var admin = _api.ClientFor(tenant.Slug, (await _api.StaffLoginAsync(tenant.Slug, email, Password)).AccessToken);

        var teacherEmail = $"t-{Guid.NewGuid():N}@example.com";
        var added = await admin.PostAsJsonAsync("/api/identity/staff",
            new { email = teacherEmail, displayName = "An Teacher", initialPassword = Password, roles = new[] { "Teacher", "Coordinator" } }, Ct);
        added.StatusCode.ShouldBe(HttpStatusCode.OK);
        var teacherId = (await added.Content.ReadFromJsonAsync<Dictionary<string, Guid>>(Ct))!["userId"];
        (await admin.PostAsJsonAsync("/api/identity/learners", new { displayName = "Learner", role = "Student" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);

        var staff = (await admin.GetItemsAsync<StaffView>("/api/identity/staff", Ct))!;
        staff.Select(s => s.DisplayName).ShouldBe(["An Teacher", "Admin Test"], ignoreOrder: true);
        staff.Single(s => s.UserId == teacherId).Roles.ShouldBe(["Coordinator", "Teacher"]);

        (await admin.PutAsJsonAsync($"/api/hr/teachers/{teacherId}/rate", new { hourlyRate = 200_000m, currency = "VND", effectiveFrom = "2027-01-01" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.PutAsJsonAsync($"/api/hr/teachers/{teacherId}/rate", new { hourlyRate = 250_000m, currency = "VND", effectiveFrom = "2027-06-01" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var rates = (await admin.GetFromJsonAsync<RateView[]>("/api/hr/rates", Ct))!;
        rates.Select(r => r.HourlyRate).ShouldBe([250_000m, 200_000m]);

        (await admin.PostAsJsonAsync("/api/billing/packages",
            new { code = "P10", name = new { vi = "Gói 10" }, paidCredits = 10, bonusCredits = 0, price = 1_000_000m, currency = "VND" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        (await admin.GetFromJsonAsync<PackageView[]>("/api/billing/packages", Ct))!.Single().Code.ShouldBe("P10");

        // A coordinator lists staff (to pick teachers); a teacher-only member cannot; another tenant sees none of this.
        var coordinator = _api.ClientFor(tenant.Slug, (await _api.StaffLoginAsync(tenant.Slug, teacherEmail, Password)).AccessToken);
        (await coordinator.GetAsync("/api/identity/staff", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var onlyTeacherEmail = $"t-{Guid.NewGuid():N}@example.com";
        (await admin.PostAsJsonAsync("/api/identity/staff",
            new { email = onlyTeacherEmail, displayName = "Bao Teacher", initialPassword = Password, roles = new[] { "Teacher" } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var teacher = _api.ClientFor(tenant.Slug, (await _api.StaffLoginAsync(tenant.Slug, onlyTeacherEmail, Password)).AccessToken);
        (await teacher.GetAsync("/api/identity/staff", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var otherEmail = $"admin-{Guid.NewGuid():N}@example.com";
        var other = await _api.CreateTenantWithAdminAsync("adm2", otherEmail, Password);
        var otherAdmin = _api.ClientFor(other.Slug, (await _api.StaffLoginAsync(other.Slug, otherEmail, Password)).AccessToken);
        (await otherAdmin.GetItemsAsync<StaffView>("/api/identity/staff", Ct))!.Length.ShouldBe(1);
        (await otherAdmin.GetFromJsonAsync<RateView[]>("/api/hr/rates", Ct))!.ShouldBeEmpty();
        (await otherAdmin.GetFromJsonAsync<PackageView[]>("/api/billing/packages", Ct))!.ShouldBeEmpty();
    }

    private sealed record StaffView(Guid UserId, string DisplayName, string? Email, string[] Roles);

    private sealed record RateView(Guid TeacherId, decimal HourlyRate, string Currency, DateOnly EffectiveFrom);

    private sealed record PackageView(Guid Id, string Code);
}
