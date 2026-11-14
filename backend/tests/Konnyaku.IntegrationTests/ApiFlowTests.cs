using System.Net;
using System.Net.Http.Json;

namespace Konnyaku.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class ApiFlowTests(PostgresFixture db) : IAsyncDisposable
{
    private const string AdminPassword = "Admin-Pass-123";
    private readonly KonnyakuApiFactory _api = new(db);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _api.DisposeAsync();

    [Fact]
    public async Task Platform_endpoints_require_the_platform_key()
    {
        var response = await _api.CreateClient().PostAsJsonAsync("/api/platform/tenants", new { slug = "nokey" }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Admin_signs_in_on_the_staff_realm_and_manages_branches()
    {
        var tenant = await _api.CreateTenantWithAdminAsync("alpha", "admin@alpha.example.com", AdminPassword);
        var signIn = await _api.StaffLoginAsync(tenant.Slug, "admin@alpha.example.com", AdminPassword);
        signIn.Audience.ShouldBe("staff");
        signIn.Roles.ShouldContain("TenantAdmin");

        var staff = _api.ClientFor(tenant.Slug, signIn.AccessToken);
        var me = await staff.GetFromJsonAsync<Dictionary<string, object>>("/api/identity/me", Ct);
        me!["tenantId"].ToString().ShouldBe(tenant.Id.ToString());

        var created = await staff.PostAsJsonAsync("/api/organization/branches",
            new { code = "Q1", name = new { vi = "Cơ sở Quận 1" }, timeZone = "Asia/Ho_Chi_Minh", isVirtual = false }, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await staff.GetFromJsonAsync<object[]>("/api/organization/branches", Ct))!.Length.ShouldBe(1);
    }

    [Fact]
    public async Task Learner_account_is_center_issued_and_cannot_reach_staff_endpoints()
    {
        var tenant = await _api.CreateTenantWithAdminAsync("beta", "admin@beta.example.com", AdminPassword);
        var admin = await _api.StaffLoginAsync(tenant.Slug, "admin@beta.example.com", AdminPassword);
        var staff = _api.ClientFor(tenant.Slug, admin.AccessToken);

        var account = await (await staff.PostAsJsonAsync("/api/identity/learners",
            new { displayName = "Học viên Test", role = "Student" }, Ct)).Content.ReadFromJsonAsync<LearnerAccount>(Ct);
        account!.Username.ShouldStartWith("hv");

        var login = await _api.ClientFor(tenant.Slug).PostAsJsonAsync("/api/identity/learn/login",
            new { username = account.Username, password = account.TemporaryPassword }, Ct);
        var learner = (await login.Content.ReadFromJsonAsync<SignInDto>(Ct))!;
        learner.Audience.ShouldBe("learn");
        learner.MustChangePassword.ShouldBeTrue();

        var learnerClient = _api.ClientFor(tenant.Slug, learner.AccessToken);
        (await learnerClient.GetAsync("/api/organization/branches", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await learnerClient.GetAsync("/api/identity/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var changed = await learnerClient.PostAsJsonAsync("/api/identity/learn/change-password",
            new { currentPassword = account.TemporaryPassword, newPassword = "New-Pass-456" }, Ct);
        changed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var relogin = await (await _api.ClientFor(tenant.Slug).PostAsJsonAsync("/api/identity/learn/login",
            new { username = account.Username, password = "New-Pass-456" }, Ct)).Content.ReadFromJsonAsync<SignInDto>(Ct);
        relogin!.MustChangePassword.ShouldBeFalse();

        // Same credentials on another center's host: unknown there.
        var other = await _api.CreateTenantWithAdminAsync("gamma", "admin@gamma.example.com", AdminPassword);
        var elsewhere = await _api.ClientFor(other.Slug).PostAsJsonAsync("/api/identity/learn/login",
            new { username = account.Username, password = "New-Pass-456" }, Ct);
        (await elsewhere.Content.ReadFromJsonAsync<ErrorDto>(Ct))!.Code.ShouldBe("identity.invalid_credentials");
    }

    [Fact]
    public async Task Token_of_one_tenant_is_rejected_on_another_tenants_host()
    {
        var a = await _api.CreateTenantWithAdminAsync("delta", "admin@delta.example.com", AdminPassword);
        var b = await _api.CreateTenantWithAdminAsync("epsilon", "admin@epsilon.example.com", AdminPassword);
        var tokenA = (await _api.StaffLoginAsync(a.Slug, "admin@delta.example.com", AdminPassword)).AccessToken;

        (await _api.ClientFor(b.Slug, tokenA).GetAsync("/api/identity/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await _api.ClientFor(a.Slug, tokenA).GetAsync("/api/identity/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account()
    {
        var tenant = await _api.CreateTenantWithAdminAsync("zeta", "admin@zeta.example.com", AdminPassword);
        var client = _api.ClientFor(tenant.Slug);
        string? lastCode = null;
        for (var i = 0; i < 5; i++)
        {
            var response = await client.PostAsJsonAsync("/api/identity/staff/login", new { email = "admin@zeta.example.com", password = "wrong" }, Ct);
            lastCode = (await response.Content.ReadFromJsonAsync<ErrorDto>(Ct))!.Code;
        }

        lastCode.ShouldBe("identity.account_locked");
        var correct = await client.PostAsJsonAsync("/api/identity/staff/login", new { email = "admin@zeta.example.com", password = AdminPassword }, Ct);
        (await correct.Content.ReadFromJsonAsync<ErrorDto>(Ct))!.Code.ShouldBe("identity.account_locked");
    }

    [Fact]
    public async Task Malformed_request_body_is_a_client_error()
    {
        var content = new StringContent("{not json", System.Text.Encoding.UTF8, "application/json");
        var response = await _api.ClientFor("any").PostAsync("/api/identity/staff/login", content, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Catalog_returns_frameworks_usable_for_a_language()
    {
        var frameworks = (await _api.CreateClient().GetFromJsonAsync<FrameworkView[]>("/api/catalog/frameworks?language=ja", Ct))!;
        frameworks.Select(f => f.Code).ShouldBe(["CEFR", "JLPT"], ignoreOrder: true);
        frameworks.Single(f => f.Code == "JLPT").Levels.Select(l => l.Code).ShouldBe(["N5", "N4", "N3", "N2", "N1"]);
    }

    private sealed record LearnerAccount(Guid UserId, string Username, string TemporaryPassword);

    private sealed record FrameworkView(string Code, LevelView[] Levels);

    private sealed record LevelView(string Code, int Ordinal);
}
