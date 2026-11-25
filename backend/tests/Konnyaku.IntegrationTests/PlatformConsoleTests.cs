using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Konnyaku.Platform.Application;

namespace Konnyaku.IntegrationTests;

/// <summary>Platform console: operator sign-in with TOTP, realm separation, admin host, centers, suspension, audit.</summary>
[Collection(PostgresCollection.Name)]
public sealed class PlatformConsoleTests(PostgresFixture db) : IAsyncDisposable
{
    private readonly KonnyakuApiFactory _api = new(db);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _api.DisposeAsync();

    [Fact]
    public async Task Bootstrap_is_single_use_and_needs_the_platform_key()
    {
        await PlatformOperators.RootTokenAsync(_api);

        (await _api.CreateClient().PostAsJsonAsync("/api/platform/operators/bootstrap",
                new { email = "x@example.com", displayName = "X", password = "Long-Password-123" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var again = await _api.PlatformClient().PostAsJsonAsync("/api/platform/operators/bootstrap",
            new { email = "x@example.com", displayName = "X", password = "Long-Password-123" }, Ct);
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await again.Content.ReadFromJsonAsync<ErrorDto>(Ct))!.Code.ShouldBe("platform.bootstrap_done");
    }

    [Fact]
    public async Task Only_a_platform_token_on_the_admin_host_reaches_the_console()
    {
        var root = await PlatformOperators.RootTokenAsync(_api);
        (await _api.AdminClient(root).GetAsync("/api/platform/console/tenants", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // No token, or only the automation key: not signed in.
        (await _api.AdminClient().GetAsync("/api/platform/console/tenants", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var keyOnly = _api.AdminClient();
        keyOnly.DefaultRequestHeaders.Add("X-Platform-Key", KonnyakuApiFactory.PlatformKey);
        (await keyOnly.GetAsync("/api/platform/console/tenants", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // A tenant administrator's staff token is refused, on the admin host and on its own host.
        var email = $"admin-{Guid.NewGuid():N}@example.com";
        var tenant = await _api.CreateTenantWithAdminAsync("pfs", email, "Admin-Pass-123");
        var staffToken = (await _api.StaffLoginAsync(tenant.Slug, email, "Admin-Pass-123")).AccessToken;
        (await _api.AdminClient(staffToken).GetAsync("/api/platform/console/tenants", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _api.ClientFor(tenant.Slug, staffToken).GetAsync("/api/platform/console/tenants", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // The platform token does not open tenant endpoints, and the console does not exist on a tenant host.
        (await _api.ClientFor(tenant.Slug, root).GetAsync("/api/identity/staff", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _api.ClientFor(tenant.Slug, root).GetAsync("/api/platform/console/tenants", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _api.ClientFor(tenant.Slug).PostAsJsonAsync("/api/platform/auth/login",
            new { email = PlatformOperators.RootEmail, password = PlatformOperators.RootPassword }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Invited_operator_enrolls_totp_and_wrong_or_replayed_codes_are_refused()
    {
        var root = await PlatformOperators.RootTokenAsync(_api);
        var email = $"op-{Guid.NewGuid():N}@example.com";
        var invited = await (await _api.AdminClient(root).PostAsJsonAsync("/api/platform/console/operators",
            new { email, displayName = "Second Operator" }, Ct)).Content.ReadFromJsonAsync<InvitedDto>(Ct);

        // First sign-in: password accepted, enrollment required; a wrong code is refused.
        var step = await LoginAsync(email, invited!.TemporaryPassword);
        step.EnrollmentRequired.ShouldBeTrue();
        step.OtpAuthUri.ShouldStartWith("otpauth://totp/");
        (await VerifyAsync(step.Challenge, code: "000000")).Code.ShouldBe("platform.invalid_code");

        var secret = Secret(step.Secret!);
        var code = Totp.Code(secret, Totp.StepAt(DateTimeOffset.UtcNow));
        var session = await VerifyOkAsync(step.Challenge, code);
        session.MustChangePassword.ShouldBeTrue();
        session.RecoveryCodes!.Count.ShouldBe(RecoveryCodes.Count);

        // A temporary password only allows changing it.
        var console = _api.AdminClient(session.AccessToken);
        var blocked = await console.GetAsync("/api/platform/console/tenants", Ct);
        blocked.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await blocked.Content.ReadFromJsonAsync<ErrorDto>(Ct))!.Code.ShouldBe("platform.password_change_required");
        (await console.PostAsJsonAsync("/api/platform/console/me/password",
            new { currentPassword = invited.TemporaryPassword, newPassword = "New-Operator-Pass-1" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await console.GetAsync("/api/platform/console/tenants", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Enrolled: the code just used cannot be replayed, a wrong one is refused, a recovery code works once.
        var second = await LoginAsync(email, "New-Operator-Pass-1");
        second.EnrollmentRequired.ShouldBeFalse();
        second.Secret.ShouldBeNull();
        (await VerifyAsync(second.Challenge, code: code)).Code.ShouldBe("platform.invalid_code");
        (await VerifyAsync(second.Challenge, code: "123456")).Code.ShouldBe("platform.invalid_code");
        var recovery = session.RecoveryCodes[0];
        (await VerifyOkAsync(second.Challenge, recoveryCode: recovery)).RecoveryCodesLeft.ShouldBe(RecoveryCodes.Count - 1);
        var third = await LoginAsync(email, "New-Operator-Pass-1");
        (await VerifyAsync(third.Challenge, recoveryCode: recovery)).Code.ShouldBe("platform.invalid_code");

        // A forged or expired challenge is not a session.
        (await VerifyAsync("not-a-token", code: "123456")).Code.ShouldBe("platform.challenge_expired");
    }

    [Fact]
    public async Task Five_failures_lock_the_operator_and_disabling_ends_access()
    {
        var root = await PlatformOperators.RootTokenAsync(_api);
        var (email, password, token) = await PlatformOperators.InviteSignedInAsync(_api, root);

        var step = await LoginAsync(email, password);
        for (var i = 0; i < 4; i++)
        {
            (await VerifyAsync(step.Challenge, code: "999999")).Code.ShouldBe("platform.invalid_code");
        }

        (await VerifyAsync(step.Challenge, code: "999999")).Code.ShouldBe("platform.account_locked");
        var locked = await _api.AdminClient().PostAsJsonAsync("/api/platform/auth/login", new { email, password }, Ct);
        (await locked.Content.ReadFromJsonAsync<ErrorDto>(Ct))!.Code.ShouldBe("platform.account_locked");

        var operators = (await _api.AdminClient(root).GetFromJsonAsync<OperatorDto[]>("/api/platform/console/operators", Ct))!;
        var id = operators.Single(o => o.Email == email).Id;
        (await _api.AdminClient(root).PostAsync($"/api/platform/console/operators/{id}/disable", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _api.AdminClient(token).GetAsync("/api/platform/console/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Operator_creates_a_center_with_its_admin_and_suspending_it_blocks_sign_in_and_api()
    {
        var root = await PlatformOperators.RootTokenAsync(_api);
        var console = _api.AdminClient(root);
        var slug = $"pfc-{Guid.NewGuid():N}"[..16];
        var adminEmail = $"owner-{Guid.NewGuid():N}@example.com";

        var created = await console.PostAsJsonAsync("/api/platform/console/tenants", new
        {
            slug,
            name = "Trung tâm Thử",
            defaultLocale = "vi",
            enabledLocales = new[] { "vi", "en" },
            timeZone = "Asia/Ho_Chi_Minh",
            adminEmail,
            adminName = "Chủ trung tâm",
        }, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var provisioned = (await created.Content.ReadFromJsonAsync<ProvisionedDto>(Ct))!;
        provisioned.Host.ShouldBe($"{slug}.{KonnyakuApiFactory.BaseDomain}");
        provisioned.TemporaryPassword.ShouldNotBeNullOrEmpty();

        // The new admin signs in on the center's host; the list shows the center and its size.
        var firstSignIn = await _api.StaffLoginAsync(slug, adminEmail, provisioned.TemporaryPassword!);
        // The operator saw the generated password: the admin must replace it at the first sign-in.
        firstSignIn.MustChangePassword.ShouldBeTrue();
        var staffToken = firstSignIn.AccessToken;
        var list = (await console.GetFromJsonAsync<TenantSummaryDto[]>("/api/platform/console/tenants", Ct))!;
        var row = list.Single(t => t.Id == provisioned.TenantId);
        row.Status.ShouldBe("Active");
        row.Usage!.Staff.ShouldBe(1);
        row.Usage.Students.ShouldBe(0);

        // Reserved and duplicate slugs are refused.
        var reserved = await console.PostAsJsonAsync("/api/platform/console/tenants", new
        {
            slug = "admin", name = "X", defaultLocale = "vi", enabledLocales = new[] { "vi" }, timeZone = "UTC", adminEmail, adminName = "X",
        }, Ct);
        (await reserved.Content.ReadFromJsonAsync<ErrorDto>(Ct))!.Code.ShouldBe("platform.slug_reserved");

        // Suspend: sign-in and calls with an existing token get a clear error; the public info says suspended.
        (await console.PostAsJsonAsync($"/api/platform/console/tenants/{provisioned.TenantId}/suspend", new { reason = "Chưa thanh toán" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var login = await _api.ClientFor(slug).PostAsJsonAsync("/api/identity/staff/login", new { email = adminEmail, password = provisioned.TemporaryPassword }, Ct);
        login.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await login.Content.ReadFromJsonAsync<ErrorDto>(Ct))!.Code.ShouldBe("platform.tenant_suspended");
        var call = await _api.ClientFor(slug, staffToken).GetAsync("/api/identity/staff", Ct);
        call.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await call.Content.ReadFromJsonAsync<ErrorDto>(Ct))!.Code.ShouldBe("platform.tenant_suspended");
        (await _api.ClientFor(slug).GetFromJsonAsync<TenantInfoDto>("/api/platform/tenant", Ct))!.Status.ShouldBe("Suspended");

        // Resume: sign-in works again.
        (await console.PostAsync($"/api/platform/console/tenants/{provisioned.TenantId}/resume", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _api.StaffLoginAsync(slug, adminEmail, provisioned.TemporaryPassword!);

        // Every step is in the audit log with the operator who did it.
        var audit = (await console.GetFromJsonAsync<AuditDto[]>($"/api/platform/console/audit?tenantId={provisioned.TenantId}", Ct))!;
        audit.Select(a => a.Action).ShouldBe(["tenant.resume", "tenant.suspend", "tenant.admin_create", "tenant.create"]);
        audit.ShouldAllBe(a => a.Actor == PlatformOperators.RootEmail);
        audit.Single(a => a.Action == "tenant.suspend").Details["reason"].ShouldBe("Chưa thanh toán");
    }

    [Fact]
    public async Task Custom_domains_resolve_to_the_center_and_base_domain_hosts_are_refused()
    {
        var root = await PlatformOperators.RootTokenAsync(_api);
        var console = _api.AdminClient(root);
        var tenant = await _api.CreateTenantWithAdminAsync("pfd", $"a-{Guid.NewGuid():N}@example.com", "Admin-Pass-123");
        var custom = $"hoc-{Guid.NewGuid():N}"[..12] + ".example.com";

        (await console.PostAsJsonAsync($"/api/platform/console/tenants/{tenant.Id}/domains", new { host = custom.ToUpperInvariant() }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var resolved = _api.CreateClient();
        resolved.DefaultRequestHeaders.Add("X-Tenant-Host", custom);
        (await resolved.GetFromJsonAsync<TenantInfoDto>("/api/platform/tenant", Ct))!.Id.ShouldBe(tenant.Id);

        var underBase = await console.PostAsJsonAsync($"/api/platform/console/tenants/{tenant.Id}/domains", new { host = $"x.{KonnyakuApiFactory.BaseDomain}" }, Ct);
        (await underBase.Content.ReadFromJsonAsync<ErrorDto>(Ct))!.Code.ShouldBe("platform.host_reserved");
        var primary = await console.PostAsJsonAsync($"/api/platform/console/tenants/{tenant.Id}/domains/remove", new { host = tenant.Hosts[0] }, Ct);
        (await primary.Content.ReadFromJsonAsync<ErrorDto>(Ct))!.Code.ShouldBe("platform.domain_primary");

        (await console.PostAsJsonAsync($"/api/platform/console/tenants/{tenant.Id}/domains/remove", new { host = custom }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await resolved.GetAsync("/api/platform/tenant", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private async Task<SignInStepDto> LoginAsync(string email, string password)
    {
        var response = await _api.AdminClient().PostAsJsonAsync("/api/platform/auth/login", new { email, password }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<SignInStepDto>(Ct))!;
    }

    private async Task<ErrorDto> VerifyAsync(string challenge, string? code = null, string? recoveryCode = null)
    {
        var response = await _api.AdminClient().PostAsJsonAsync("/api/platform/auth/verify", new { challenge, code, recoveryCode }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        return (await response.Content.ReadFromJsonAsync<ErrorDto>(Ct))!;
    }

    private async Task<OperatorSessionDto> VerifyOkAsync(string challenge, string? code = null, string? recoveryCode = null)
    {
        var response = await _api.AdminClient().PostAsJsonAsync("/api/platform/auth/verify", new { challenge, code, recoveryCode }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<OperatorSessionDto>(Ct))!;
    }

    private static byte[] Secret(string base32) => Base32.TryDecode(base32, out var bytes) ? bytes : throw new InvalidOperationException();

    private sealed record InvitedDto(Guid Id, string Email, string TemporaryPassword);

    private sealed record OperatorDto(Guid Id, string Email, string Status);

    private sealed record ProvisionedDto(Guid TenantId, string Slug, string Host, string AdminEmail, string? TemporaryPassword);

    private sealed record UsageDto(int Students, int ActiveClasses, int Staff);

    private sealed record TenantSummaryDto(Guid Id, string Slug, string Status, UsageDto? Usage);

    private sealed record TenantInfoDto(Guid Id, string Slug, string Status);

    private sealed record AuditDto(string Actor, string Action, Guid? TenantId, Dictionary<string, string?> Details);
}

public sealed record SignInStepDto(string Challenge, bool EnrollmentRequired, string? Secret, string? OtpAuthUri);

public sealed record OperatorSessionDto(string AccessToken, bool MustChangePassword, List<string>? RecoveryCodes, int RecoveryCodesLeft);

/// <summary>
/// The first operator is bootstrapped once per test database and signed in once per test run: a TOTP code is
/// accepted only once, so tests needing fresh sign-ins invite their own operators.
/// </summary>
public static class PlatformOperators
{
    public const string RootEmail = "root-operator@example.com";
    public const string RootPassword = "Root-Operator-Pass-1";
    public const string RootSecret = "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string? _rootToken;

    public static async Task<string> RootTokenAsync(KonnyakuApiFactory api)
    {
        await Gate.WaitAsync();
        try
        {
            if (_rootToken is not null)
            {
                return _rootToken;
            }

            var bootstrap = await api.PlatformClient().PostAsJsonAsync("/api/platform/operators/bootstrap",
                new { email = RootEmail, displayName = "Root Operator", password = RootPassword, totpSecret = RootSecret });
            if (bootstrap.StatusCode != HttpStatusCode.Conflict)
            {
                bootstrap.EnsureSuccessStatusCode();
            }

            var step = await (await api.AdminClient().PostAsJsonAsync("/api/platform/auth/login", new { email = RootEmail, password = RootPassword }))
                .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<SignInStepDto>();
            Base32.TryDecode(RootSecret, out var secret);
            var session = await (await api.AdminClient().PostAsJsonAsync("/api/platform/auth/verify",
                    new { challenge = step!.Challenge, code = Totp.Code(secret, Totp.StepAt(DateTimeOffset.UtcNow)) }))
                .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<OperatorSessionDto>();
            return _rootToken = session!.AccessToken;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>A new operator, enrolled and with a permanent password: (email, password, access token).</summary>
    public static async Task<(string Email, string Password, string Token)> InviteSignedInAsync(KonnyakuApiFactory api, string rootToken)
    {
        var email = $"op-{Guid.NewGuid():N}@example.com";
        var invited = await (await api.AdminClient(rootToken).PostAsJsonAsync("/api/platform/console/operators", new { email, displayName = "Operator" }))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<Dictionary<string, object>>();
        var temporary = invited!["temporaryPassword"].ToString()!;
        var step = await (await api.AdminClient().PostAsJsonAsync("/api/platform/auth/login", new { email, password = temporary }))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<SignInStepDto>();
        Base32.TryDecode(step!.Secret, out var secret);
        var session = await (await api.AdminClient().PostAsJsonAsync("/api/platform/auth/verify",
                new { challenge = step.Challenge, code = Totp.Code(secret, Totp.StepAt(DateTimeOffset.UtcNow)) }))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<OperatorSessionDto>();
        const string password = "Operator-Pass-1234";
        var client = api.AdminClient(session!.AccessToken);
        (await client.PostAsJsonAsync("/api/platform/console/me/password", new { currentPassword = temporary, newPassword = password }))
            .EnsureSuccessStatusCode();
        return (email, password, session.AccessToken);
    }

    public static HttpClient AdminClient(this KonnyakuApiFactory api, string? accessToken = null)
    {
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Host", KonnyakuApiFactory.AdminHost);
        if (accessToken is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return client;
    }
}
