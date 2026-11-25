using System.Net;
using System.Net.Http.Json;
using Konnyaku.Application.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Konnyaku.IntegrationTests;

/// <summary>
/// Account lifecycle (P2): sign-in sessions and their revocation, locking, password change / reset / forgot, 2FA and the
/// security log. Revocations are checked through the API itself (the session cache of this process is cleared on revoke).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AccountSecurityTests(PostgresFixture db) : IAsyncDisposable
{
    private const string Password = EditCenter.Password;
    private readonly KonnyakuApiFactory _api = new(db);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _api.DisposeAsync();

    [Fact]
    public async Task Signing_out_revokes_the_token()
    {
        var c = await EditCenter.CreateAsync(_api, "sess");
        var (email, _) = await AddStaffAsync(c, "Teacher");
        var token = (await _api.StaffLoginAsync(c.Slug, email, Password)).AccessToken;
        var client = _api.ClientFor(c.Slug, token);

        (await client.GetAsync("/api/identity/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var security = (await client.GetFromJsonAsync<MySecurity>("/api/identity/me/security", Ct))!;
        security.Sessions.ShouldContain(s => s.Current && s.Audience == "staff");

        (await client.PostAsync("/api/identity/logout", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.GetAsync("/api/identity/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Locking_an_account_signs_it_out_and_refuses_sign_in()
    {
        var c = await EditCenter.CreateAsync(_api, "lock");
        var learner = await CreateLearnerAsync(c);
        var learnerClient = _api.ClientFor(c.Slug, (await LearnerLoginAsync(c, learner.Username, learner.TemporaryPassword)).AccessToken);
        (await learnerClient.GetAsync("/api/identity/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await c.Admin.PostAsync($"/api/identity/accounts/{learner.UserId}/suspend", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await learnerClient.GetAsync("/api/identity/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await (await _api.ClientFor(c.Slug).PostAsJsonAsync("/api/identity/learn/login", new { username = learner.Username, password = learner.TemporaryPassword }, Ct))
            .ShouldFailWith(HttpStatusCode.BadRequest, "identity.account_suspended");
        var accounts = (await c.Admin.GetFromJsonAsync<LearnerAccount[]>("/api/identity/accounts/learners", Ct))!;
        accounts.Single(a => a.UserId == learner.UserId).Status.ShouldBe("Suspended");

        (await c.Admin.PostAsync($"/api/identity/accounts/{learner.UserId}/activate", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await LearnerLoginAsync(c, learner.Username, learner.TemporaryPassword);

        // Staff: deactivating from the staff screen ends the sessions too.
        var (teacherEmail, teacherId) = await AddStaffAsync(c, "Teacher");
        var teacher = _api.ClientFor(c.Slug, (await _api.StaffLoginAsync(c.Slug, teacherEmail, Password)).AccessToken);
        (await c.Admin.PostAsync($"/api/identity/staff/{teacherId}/deactivate", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await teacher.GetAsync("/api/identity/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var events = (await c.Admin.GetFromJsonAsync<SecurityEventView[]>("/api/identity/security-events", Ct))!;
        events.ShouldContain(e => e.Kind == "AccountSuspended" && e.UserId == learner.UserId && e.ActorId == c.AdminId);
        events.ShouldContain(e => e.Kind == "AccountReactivated" && e.UserId == learner.UserId);
        events.ShouldContain(e => e.Kind == "SignInFailed" && e.UserId == learner.UserId && e.Detail == "suspended");
        events.ShouldContain(e => e.Kind == "SignInSucceeded" && e.UserId == teacherId);
    }

    [Fact]
    public async Task Changing_the_password_keeps_this_device_and_ends_the_others()
    {
        var c = await EditCenter.CreateAsync(_api, "chpw");
        var (email, _) = await AddStaffAsync(c, "Teacher");
        var here = _api.ClientFor(c.Slug, (await _api.StaffLoginAsync(c.Slug, email, Password)).AccessToken);
        var there = _api.ClientFor(c.Slug, (await _api.StaffLoginAsync(c.Slug, email, Password)).AccessToken);

        await (await here.PostAsJsonAsync("/api/identity/staff/change-password", new { currentPassword = Password, newPassword = "onlyletters" }, Ct))
            .ShouldFailWith(HttpStatusCode.BadRequest, "identity.password_too_weak");
        await (await here.PostAsJsonAsync("/api/identity/staff/change-password", new { currentPassword = Password, newPassword = Password }, Ct))
            .ShouldFailWith(HttpStatusCode.BadRequest, "identity.password_unchanged");
        (await here.PostAsJsonAsync("/api/identity/staff/change-password", new { currentPassword = Password, newPassword = "New-Pass-456" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await here.GetAsync("/api/identity/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await there.GetAsync("/api/identity/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await (await _api.ClientFor(c.Slug).PostAsJsonAsync("/api/identity/staff/login", new { email, password = Password }, Ct))
            .ShouldFailWith(HttpStatusCode.BadRequest, "identity.invalid_credentials");
        await _api.StaffLoginAsync(c.Slug, email, "New-Pass-456");
    }

    [Fact]
    public async Task Administrators_reset_passwords_with_a_temporary_one()
    {
        var c = await EditCenter.CreateAsync(_api, "rspw");
        var (email, teacherId) = await AddStaffAsync(c, "Teacher");
        var teacher = _api.ClientFor(c.Slug, (await _api.StaffLoginAsync(c.Slug, email, Password)).AccessToken);

        var reset = await (await c.Admin.PostAsync($"/api/identity/accounts/{teacherId}/reset-password", null, Ct)).Content
            .ReadFromJsonAsync<AdminReset>(Ct);
        reset!.TemporaryPassword.Length.ShouldBe(10);
        (await teacher.GetAsync("/api/identity/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var signIn = await _api.StaffLoginAsync(c.Slug, email, reset.TemporaryPassword);
        signIn.MustChangePassword.ShouldBeTrue();

        var learner = await CreateLearnerAsync(c);
        var learnerReset = await (await c.Admin.PostAsync($"/api/identity/accounts/{learner.UserId}/reset-password", null, Ct)).Content
            .ReadFromJsonAsync<AdminReset>(Ct);
        learnerReset!.Username.ShouldBe(learner.Username);
        (await LearnerLoginAsync(c, learner.Username, learnerReset.TemporaryPassword)).MustChangePassword.ShouldBeTrue();

        await (await c.Admin.PostAsync($"/api/identity/accounts/{c.AdminId}/reset-password", null, Ct))
            .ShouldFailWith(HttpStatusCode.Conflict, "identity.cannot_reset_self");

        // A teacher who also works for another center keeps one password: this center cannot reset it, and a change made
        // on one center signs them out of the other.
        var other = await EditCenter.CreateAsync(_api, "rspw2");
        (await other.Admin.PostAsJsonAsync("/api/identity/staff", new { email, displayName = "Shared", roles = new[] { "Teacher" } }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        await (await c.Admin.PostAsync($"/api/identity/accounts/{teacherId}/reset-password", null, Ct))
            .ShouldFailWith(HttpStatusCode.Conflict, "identity.account_shared_with_other_centers");
        var atOther = _api.ClientFor(other.Slug, (await _api.StaffLoginAsync(other.Slug, email, reset.TemporaryPassword)).AccessToken);
        var atFirst = _api.ClientFor(c.Slug, signIn.AccessToken);
        (await atFirst.PostAsJsonAsync("/api/identity/staff/change-password", new { currentPassword = reset.TemporaryPassword, newPassword = "Own-Pass-789" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await atOther.GetAsync("/api/identity/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await atFirst.GetAsync("/api/identity/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Forgot_password_sends_a_single_use_link_without_revealing_accounts()
    {
        var c = await EditCenter.CreateAsync(_api, "forgot");
        var (email, teacherId) = await AddStaffAsync(c, "Teacher");
        var before = _api.ClientFor(c.Slug, (await _api.StaffLoginAsync(c.Slug, email, Password)).AccessToken);
        var mailbox = _api.Services.GetRequiredService<TestMailbox>();
        var anonymous = _api.ClientFor(c.Slug);

        (await anonymous.PostAsJsonAsync("/api/identity/password/forgot", new { email = $"nobody-{Guid.NewGuid():N}@example.com" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await anonymous.PostAsJsonAsync("/api/identity/password/forgot", new { email = email.ToUpperInvariant() }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var mail = mailbox.Sent.Single(m => m.UserId == teacherId);
        mail.ResetUrl.ShouldStartWith($"https://{c.Slug}.{KonnyakuApiFactory.BaseDomain}/login/reset?token=");
        var token = Uri.UnescapeDataString(mail.ResetUrl.Split("token=")[1]);

        await (await anonymous.PostAsJsonAsync("/api/identity/password/reset", new { token, newPassword = "short" }, Ct))
            .ShouldFailWith(HttpStatusCode.BadRequest, "identity.password_too_short");
        (await anonymous.PostAsJsonAsync("/api/identity/password/reset", new { token, newPassword = "Reset-Pass-321" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await (await anonymous.PostAsJsonAsync("/api/identity/password/reset", new { token, newPassword = "Again-Pass-321" }, Ct))
            .ShouldFailWith(HttpStatusCode.BadRequest, "identity.reset_link_invalid");
        (await before.GetAsync("/api/identity/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await _api.StaffLoginAsync(c.Slug, email, "Reset-Pass-321");

        // At most three links per account and hour (the answer stays 202).
        for (var i = 0; i < 4; i++)
        {
            (await anonymous.PostAsJsonAsync("/api/identity/password/forgot", new { email }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        mailbox.Sent.Count(m => m.UserId == teacherId).ShouldBe(3);
        var events = (await c.Admin.GetFromJsonAsync<SecurityEventView[]>($"/api/identity/security-events?userId={teacherId}", Ct))!;
        events.ShouldContain(e => e.Kind == "PasswordResetCompleted");
        events.ShouldContain(e => e.Kind == "PasswordResetRequested" && e.Detail == "throttled");
    }

    [Fact]
    public async Task Two_factor_sign_in_with_a_code_or_a_recovery_code()
    {
        var c = await EditCenter.CreateAsync(_api, "totp");
        var (email, teacherId) = await AddStaffAsync(c, "Teacher");
        var teacher = _api.ClientFor(c.Slug, (await _api.StaffLoginAsync(c.Slug, email, Password)).AccessToken);

        var setup = (await (await teacher.PostAsync("/api/identity/me/2fa/setup", null, Ct)).Content.ReadFromJsonAsync<TwoFactorSetup>(Ct))!;
        setup.OtpAuthUri.ShouldStartWith("otpauth://totp/");
        Base32.TryDecode(setup.Secret, out var secret).ShouldBeTrue();
        await (await teacher.PostAsJsonAsync("/api/identity/me/2fa/enable", new { code = "000000" }, Ct))
            .ShouldFailWith(HttpStatusCode.BadRequest, "identity.code_invalid");
        var enabled = (await (await teacher.PostAsJsonAsync("/api/identity/me/2fa/enable", new { code = CodeAt(secret, 0) }, Ct)).Content
            .ReadFromJsonAsync<RecoveryCodesView>(Ct))!;
        enabled.RecoveryCodes.Length.ShouldBe(10);

        var anonymous = _api.ClientFor(c.Slug);
        var first = await _api.StaffLoginAsync(c.Slug, email, Password);
        first.AccessToken.ShouldBeNull();
        first.NextStep.ShouldBe("verify");
        await (await anonymous.PostAsJsonAsync("/api/identity/staff/login/2fa", new { challenge = first.Challenge, code = "123456" }, Ct))
            .ShouldFailWith(HttpStatusCode.BadRequest, "identity.code_invalid");
        // The enrollment used this time step's code; the next step's code is still accepted (one step of drift).
        var signedIn = await (await anonymous.PostAsJsonAsync("/api/identity/staff/login/2fa", new { challenge = first.Challenge, code = CodeAt(secret, 1) }, Ct))
            .Content.ReadFromJsonAsync<SignInDto>(Ct);
        (await _api.ClientFor(c.Slug, signedIn!.AccessToken).GetAsync("/api/identity/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var second = await _api.StaffLoginAsync(c.Slug, email, Password);
        (await anonymous.PostAsJsonAsync("/api/identity/staff/login/2fa", new { challenge = second.Challenge, recoveryCode = enabled.RecoveryCodes[0] }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var third = await _api.StaffLoginAsync(c.Slug, email, Password);
        await (await anonymous.PostAsJsonAsync("/api/identity/staff/login/2fa", new { challenge = third.Challenge, recoveryCode = enabled.RecoveryCodes[0] }, Ct))
            .ShouldFailWith(HttpStatusCode.BadRequest, "identity.code_invalid");

        // Lost phone: the administrator removes 2FA (logged), the teacher signs in with the password again.
        (await c.Admin.PostAsync($"/api/identity/accounts/{teacherId}/reset-2fa", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await _api.StaffLoginAsync(c.Slug, email, Password)).AccessToken.ShouldNotBeNull();
        var events = (await c.Admin.GetFromJsonAsync<SecurityEventView[]>($"/api/identity/security-events?userId={teacherId}", Ct))!;
        events.Select(e => e.Kind).ShouldContain("TwoFactorEnabled");
        events.Select(e => e.Kind).ShouldContain("RecoveryCodeUsed");
        events.Select(e => e.Kind).ShouldContain("TwoFactorResetByAdmin");
        (await c.HistoryAsync("identity", teacherId)).ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Administrators_and_accountants_must_enroll_2fa_when_enforced()
    {
        await using var enforced = _api.WithWebHostBuilder(b => b.UseSetting("Auth:TwoFactor:Enforce", "true"));
        var email = $"admin-{Guid.NewGuid():N}@example.com";
        var tenant = await _api.CreateTenantWithAdminAsync("mfa", email, Password);
        var client = enforced.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Host", $"{tenant.Slug}.{KonnyakuApiFactory.BaseDomain}");

        var first = (await (await client.PostAsJsonAsync("/api/identity/staff/login", new { email, password = Password }, Ct)).Content
            .ReadFromJsonAsync<SignInDto>(Ct))!;
        first.AccessToken.ShouldBeNull();
        first.NextStep.ShouldBe("enroll");
        await (await client.PostAsJsonAsync("/api/identity/staff/login/2fa", new { challenge = first.Challenge, code = "000000" }, Ct))
            .ShouldFailWith(HttpStatusCode.BadRequest, "identity.sign_in_expired");

        var setup = (await (await client.PostAsJsonAsync("/api/identity/staff/login/2fa/setup", new { challenge = first.Challenge }, Ct)).Content
            .ReadFromJsonAsync<TwoFactorSetup>(Ct))!;
        Base32.TryDecode(setup.Secret, out var secret).ShouldBeTrue();
        var enrolled = (await (await client.PostAsJsonAsync("/api/identity/staff/login/2fa/enable", new { challenge = first.Challenge, code = CodeAt(secret, 0) }, Ct))
            .Content.ReadFromJsonAsync<EnrolledView>(Ct))!;
        enrolled.RecoveryCodes.Length.ShouldBe(10);

        var admin = enforced.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Tenant-Host", $"{tenant.Slug}.{KonnyakuApiFactory.BaseDomain}");
        admin.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", enrolled.SignIn.AccessToken);
        (await admin.GetFromJsonAsync<MySecurity>("/api/identity/me/security", Ct))!.TwoFactorRequired.ShouldBeTrue();
        await (await admin.PostAsJsonAsync("/api/identity/me/2fa/disable", new { password = Password, code = CodeAt(secret, 1) }, Ct))
            .ShouldFailWith(HttpStatusCode.Conflict, "identity.two_factor_required");
    }

    [Fact]
    public async Task Changing_roles_signs_the_member_out()
    {
        var c = await EditCenter.CreateAsync(_api, "roles");
        var (email, teacherId) = await AddStaffAsync(c, "Teacher");
        var teacher = _api.ClientFor(c.Slug, (await _api.StaffLoginAsync(c.Slug, email, Password)).AccessToken);

        (await c.Admin.PutAsJsonAsync($"/api/identity/staff/{teacherId}", new { displayName = "Teacher Test", roles = new[] { new { role = "Teacher" } } }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await teacher.GetAsync("/api/identity/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await c.Admin.PutAsJsonAsync($"/api/identity/staff/{teacherId}",
                new { displayName = "Teacher Test", roles = new object[] { new { role = "Teacher" }, new { role = "Consultant" } } }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await teacher.GetAsync("/api/identity/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Account_tools_are_for_administrators_only()
    {
        var c = await EditCenter.CreateAsync(_api, "acct");
        var learner = await CreateLearnerAsync(c);
        foreach (var role in new[] { "BranchManager", "Coordinator", "Accountant", "Consultant", "Teacher" })
        {
            var (_, member) = await c.AddStaffAsync(role);
            await (await member.GetAsync("/api/identity/accounts/learners", Ct)).ShouldFailWith(HttpStatusCode.Forbidden, "access.forbidden");
            await (await member.GetAsync("/api/identity/security-events", Ct)).ShouldFailWith(HttpStatusCode.Forbidden, "access.forbidden");
            await (await member.PostAsync($"/api/identity/accounts/{learner.UserId}/suspend", null, Ct)).ShouldFailWith(HttpStatusCode.Forbidden, "access.forbidden");
            await (await member.PostAsync($"/api/identity/accounts/{learner.UserId}/reset-password", null, Ct)).ShouldFailWith(HttpStatusCode.Forbidden, "access.forbidden");
            await (await member.PostAsync($"/api/identity/accounts/{c.TeacherId}/reset-2fa", null, Ct)).ShouldFailWith(HttpStatusCode.Forbidden, "access.forbidden");
            await (await member.PostAsync($"/api/identity/accounts/{c.TeacherId}/sign-out", null, Ct)).ShouldFailWith(HttpStatusCode.Forbidden, "access.forbidden");
        }

        // A learner token never reaches staff endpoints.
        var learnerClient = _api.ClientFor(c.Slug, (await LearnerLoginAsync(c, learner.Username, learner.TemporaryPassword)).AccessToken);
        (await learnerClient.GetAsync("/api/identity/accounts/learners", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await learnerClient.PostAsJsonAsync("/api/identity/staff/change-password", new { currentPassword = "x", newPassword = "y" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private async Task<(string Email, Guid UserId)> AddStaffAsync(EditCenter c, string role)
    {
        var email = $"{role.ToLowerInvariant()}-{Guid.NewGuid():N}@example.com";
        var response = await c.Admin.PostAsJsonAsync("/api/identity/staff",
            new { email, displayName = $"{role} Test", initialPassword = Password, roles = new[] { role } }, Ct);
        await response.ShouldSucceed();
        return (email, (await response.Content.ReadFromJsonAsync<UserIdView>(Ct))!.UserId);
    }

    private static async Task<LearnerCreated> CreateLearnerAsync(EditCenter c)
    {
        var response = await c.Admin.PostAsJsonAsync("/api/identity/learners", new { displayName = "Học viên Bảo mật", role = "Student" }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<LearnerCreated>(Ct))!;
    }

    private async Task<SignInDto> LearnerLoginAsync(EditCenter c, string username, string password)
    {
        var response = await _api.ClientFor(c.Slug).PostAsJsonAsync("/api/identity/learn/login", new { username, password }, Ct);
        await response.ShouldSucceed();
        return (await response.Content.ReadFromJsonAsync<SignInDto>(Ct))!;
    }

    private static string CodeAt(byte[] secret, int stepsAhead) => Totp.Code(secret, Totp.StepAt(DateTimeOffset.UtcNow) + stepsAhead);

    private sealed record LearnerCreated(Guid UserId, string Username, string TemporaryPassword);

    private sealed record LearnerAccount(Guid UserId, string Username, string Status, bool MustChangePassword);

    private sealed record AdminReset(string TemporaryPassword, string? Username, string? Email);

    private sealed record SessionItem(Guid Id, string Audience, bool Current);

    private sealed record MySecurity(bool TwoFactorEnabled, bool TwoFactorRequired, int RecoveryCodesLeft, bool MustChangePassword, SessionItem[] Sessions);

    private sealed record SecurityEventView(Guid Id, string Kind, Guid? UserId, Guid? ActorId, string? Detail);

    private sealed record TwoFactorSetup(string Secret, string OtpAuthUri);

    private sealed record RecoveryCodesView(string[] RecoveryCodes);

    private sealed record EnrolledView(string[] RecoveryCodes, SignInDto SignIn);
}
