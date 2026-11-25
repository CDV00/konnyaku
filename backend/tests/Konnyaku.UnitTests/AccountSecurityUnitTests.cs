using System.Text;
using Konnyaku.Application.Authorization;
using Konnyaku.Application.Security;
using Konnyaku.Identity.Domain;

namespace Konnyaku.UnitTests;

public class AccountSecurityUnitTests
{
    [Theory]
    [InlineData("short1", "identity.password_too_short")]
    [InlineData("onlyletters", "identity.password_too_weak")]
    [InlineData("1234567890", "identity.password_too_weak")]
    [InlineData("Password123", "identity.password_too_weak")]
    [InlineData("Review-Pass-123", null)]
    [InlineData("hoc vien 2027", null)]
    public void Password_policy_is_shared_by_both_realms(string password, string? expected) =>
        PasswordPolicy.Check(password)?.Code.ShouldBe(expected);

    [Fact]
    public void Password_policy_caps_the_length() =>
        PasswordPolicy.Check(new string('a', 128) + "1")!.Code.ShouldBe("identity.password_too_long");

    [Fact]
    public void Totp_matches_the_rfc_6238_vector()
    {
        // RFC 6238 appendix B, SHA-1 secret "12345678901234567890": 94287082 at T=59s (6 digits: 287082).
        var secret = Encoding.ASCII.GetBytes("12345678901234567890");
        var at = DateTimeOffset.FromUnixTimeSeconds(59);
        Totp.Code(secret, Totp.StepAt(at)).ShouldBe("287082");
        Totp.Match(secret, "287 082", at).ShouldBe(Totp.StepAt(at));
        Totp.Match(secret, "287082", at.AddMinutes(5)).ShouldBeNull();
    }

    [Fact]
    public void Base32_round_trips_a_secret()
    {
        var secret = Totp.NewSecret();
        Base32.TryDecode(Base32.Encode(secret), out var decoded).ShouldBeTrue();
        decoded.ShouldBe(secret);
    }

    [Fact]
    public void Recovery_codes_ignore_case_and_dashes() =>
        RecoveryCodes.Hash("ABCDE-fghij").ShouldBe(RecoveryCodes.Hash("abcdefghij"));

    [Fact]
    public void A_totp_code_is_accepted_once()
    {
        var credential = new PasswordCredential(Guid.CreateVersion7(), "hash");
        credential.BeginTotpEnrollment("protected");
        credential.EnableTotp(100, [RecoveryCodes.Hash("aaaaa-bbbbb")], DateTimeOffset.UtcNow);

        credential.AcceptTotpStep(100).ShouldBeFalse();
        credential.AcceptTotpStep(101).ShouldBeTrue();
        credential.AcceptTotpStep(101).ShouldBeFalse();
        credential.UseRecoveryCode(RecoveryCodes.Hash("aaaaa-bbbbb")).ShouldBeTrue();
        credential.UseRecoveryCode(RecoveryCodes.Hash("aaaaa-bbbbb")).ShouldBeFalse();
    }

    [Fact]
    public void A_new_password_renews_the_security_stamp()
    {
        var credential = new PasswordCredential(Guid.CreateVersion7(), "hash");
        var before = credential.SecurityStamp;
        credential.SetPassword("other", temporary: true, DateTimeOffset.UtcNow);
        credential.SecurityStamp.ShouldNotBe(before);
        credential.MustChangePassword.ShouldBeTrue();
    }

    [Theory]
    [InlineData("TenantAdmin", Permissions.Accounts, true)]
    [InlineData("BranchManager", Permissions.Accounts, false)]
    [InlineData("Accountant", Permissions.Accounts, false)]
    [InlineData("Accountant", Permissions.Payroll, true)]
    [InlineData("Accountant", Permissions.Finance, true)]
    [InlineData("Accountant", Permissions.Sales, true)]
    [InlineData("Consultant", Permissions.Sales, true)]
    [InlineData("Consultant", Permissions.Finance, false)]
    [InlineData("Consultant", Permissions.Payroll, false)]
    [InlineData("Coordinator", Permissions.Sales, false)]
    [InlineData("Teacher", Permissions.Sales, false)]
    [InlineData("Teacher", Permissions.Payroll, false)]
    public void Account_and_money_permissions(string role, string permission, bool granted) =>
        RolePermissions.Grants(role, permission).ShouldBe(granted);
}
