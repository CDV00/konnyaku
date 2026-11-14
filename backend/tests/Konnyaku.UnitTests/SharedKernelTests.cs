using Konnyaku.Organization.Domain;
using Konnyaku.SharedKernel;

namespace Konnyaku.UnitTests;

public class LocalizedStringTests
{
    private static readonly LocalizedString Name = new(new Dictionary<string, string>
    {
        ["vi"] = "Tiếng Nhật giao tiếp",
        ["en"] = "Conversational Japanese",
        ["zh"] = "日语会话",
    });

    [Theory]
    [InlineData("en", "Conversational Japanese")]
    [InlineData("EN", "Conversational Japanese")]
    [InlineData("zh-Hans", "日语会话")]
    [InlineData("ko", "Tiếng Nhật giao tiếp")]
    public void Get_falls_back_from_exact_locale_to_language_to_tenant_default(string locale, string expected) =>
        Name.Get(locale, fallbackLocale: "vi").ShouldBe(expected);

    [Fact]
    public void Blank_values_are_dropped_and_all_blank_is_rejected()
    {
        Should.Throw<ArgumentException>(() => new LocalizedString(new Dictionary<string, string> { ["vi"] = " " }));
        new LocalizedString(new Dictionary<string, string> { ["vi"] = "A", ["en"] = "" }).Values.Keys.ShouldBe(["vi"]);
    }
}

public class BranchTests
{
    private static readonly Dictionary<string, string> Name = new() { ["vi"] = "Cơ sở Quận 1" };

    [Fact]
    public void Create_normalizes_code_and_accepts_iana_time_zone()
    {
        var result = Branch.Create(" q1-hcm ", Name, "Asia/Ho_Chi_Minh", isVirtual: false);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Code.ShouldBe("Q1-HCM");
        result.Value.TimeZone.ShouldBe("Asia/Ho_Chi_Minh");
    }

    [Theory]
    [InlineData("x", "Asia/Ho_Chi_Minh", "organization.branch_code_invalid")]
    [InlineData("Q1", "Mars/Olympus", "organization.time_zone_invalid")]
    public void Create_rejects_invalid_input(string code, string timeZone, string expectedError) =>
        Branch.Create(code, Name, timeZone, isVirtual: false).Error!.Code.ShouldBe(expectedError);

    [Fact]
    public void Create_requires_a_name() =>
        Branch.Create("Q1", new Dictionary<string, string>(), "Asia/Ho_Chi_Minh", false).Error!.Code
            .ShouldBe("organization.branch_name_required");
}
