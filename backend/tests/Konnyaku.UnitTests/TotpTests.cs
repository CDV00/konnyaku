using System.Text;
using Konnyaku.Platform.Application;

namespace Konnyaku.UnitTests;

public class TotpTests
{
    // RFC 6238 appendix B (SHA-1 secret), last 6 of the 8 published digits.
    private static readonly byte[] RfcSecret = Encoding.ASCII.GetBytes("12345678901234567890");

    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    public void Codes_match_the_rfc_test_vectors(long unixSeconds, string expected) =>
        Totp.Code(RfcSecret, Totp.StepAt(DateTimeOffset.FromUnixTimeSeconds(unixSeconds))).ShouldBe(expected);

    [Fact]
    public void A_code_is_accepted_one_step_either_side_and_not_further()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var step = Totp.StepAt(now);
        Totp.Match(RfcSecret, Totp.Code(RfcSecret, step - 1), now).ShouldBe(step - 1);
        Totp.Match(RfcSecret, Totp.Code(RfcSecret, step + 1), now).ShouldBe(step + 1);
        Totp.Match(RfcSecret, Totp.Code(RfcSecret, step - 2), now).ShouldBeNull();
        Totp.Match(RfcSecret, "12345", now).ShouldBeNull();
        Totp.Match(RfcSecret, null, now).ShouldBeNull();
    }

    [Fact]
    public void Base32_round_trips_and_rejects_foreign_characters()
    {
        var secret = Totp.NewSecret();
        Base32.TryDecode(Base32.Encode(secret).ToLowerInvariant(), out var decoded).ShouldBeTrue();
        decoded.ShouldBe(secret);
        Base32.Encode(Encoding.ASCII.GetBytes("foobar")).ShouldBe("MZXW6YTBOI");
        Base32.TryDecode("NOT-BASE32-189", out _).ShouldBeFalse();
    }

    [Fact]
    public void Recovery_codes_are_unique_and_hashed_ignoring_format()
    {
        var codes = RecoveryCodes.Generate();
        codes.Distinct().Count().ShouldBe(RecoveryCodes.Count);
        RecoveryCodes.Hash(codes[0]).ShouldBe(RecoveryCodes.Hash(codes[0].ToUpperInvariant().Replace("-", " ")));
    }
}
