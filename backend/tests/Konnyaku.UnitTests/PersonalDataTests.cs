using Konnyaku.Crm.Application;
using Konnyaku.SharedKernel;

namespace Konnyaku.UnitTests;

public class PersonalDataMaskTests
{
    [Theory]
    [InlineData("0901234567", "09xx xxx 567")]
    [InlineData("09012345678", "09xx xxxx 678")]
    [InlineData("+84 901 234 567", "84xx xxxx 567")]
    [InlineData("123", "xxx")]
    [InlineData("1234567", "12xx567")]
    public void Phone_keeps_the_start_and_the_last_three_digits(string phone, string masked) =>
        PersonalDataMask.Phone(phone).ShouldBe(masked);

    [Fact]
    public void Email_name_and_birth_year_keep_just_enough_to_tell_records_apart()
    {
        PersonalDataMask.Email("an.nguyen@example.com").ShouldBe("a***@example.com");
        PersonalDataMask.Email("broken").ShouldBe("***");
        PersonalDataMask.Name("Nguyễn Văn Ân").ShouldBe("Nguyễn V** Â**");
        PersonalDataMask.Name("An").ShouldBe("A**");
        PersonalDataMask.BirthYear(2014).ShouldBe("201*");
        PersonalDataMask.Mask(PersonalDataKind.BirthYear, 1990).ShouldBe("199*");
        PersonalDataMask.Mask(PersonalDataKind.Phone, null).ShouldBeNull();
        PersonalDataMask.Term("0901234567").ShouldBe("09***");
    }
}

public class TextSearchTests
{
    [Theory]
    [InlineData("Ân", "an")]
    [InlineData("  Nguyễn Văn ĐẠT ", "nguyen van dat")]
    [InlineData("Phạm Thị Hồng Nhung", "pham thi hong nhung")]
    [InlineData("Müller", "muller")]
    public void Fold_removes_case_and_accents(string value, string folded) => TextSearch.Fold(value).ShouldBe(folded);

    [Theory]
    [InlineData("+84 901 234", "0901234")]
    [InlineData("0901.234.567", "0901234567")]
    [InlineData("12", "12")]
    [InlineData("Ân 2", "an 2")]
    public void Term_turns_a_typed_phone_into_stored_digits(string query, string term) => TextSearch.Term(query).ShouldBe(term);

    [Fact]
    public void Empty_terms_search_nothing_and_wildcards_are_escaped()
    {
        TextSearch.Term("   ").ShouldBeNull();
        TextSearch.Contains("50%_a").ShouldBe("%50\\%\\_a%");
    }

    [Fact]
    public void Paging_is_bounded()
    {
        PageRequest.Of(0, 0).ShouldBe(new PageRequest(1, 1, null, false));
        PageRequest.Of(3, 10_000, "name", "DESC").ShouldBe(new PageRequest(3, 200, "name", true));
        PageRequest.Of(null, null).Skip.ShouldBe(0);
        Page<int>.FromList([1, 2, 3, 4, 5], PageRequest.Of(2, 2)).ShouldSatisfyAllConditions(
            p => p.Items.ShouldBe([3, 4]), p => p.Total.ShouldBe(5));
    }
}

public class CsvWriterTests
{
    [Fact]
    public void Cells_are_quoted_and_formulas_neutralized()
    {
        var csv = new CsvWriter();
        csv.Row("Lê, An", "say \"hi\"", "=HYPERLINK(\"x\")", null, "0901");
        csv.ToString().ShouldBe("\"Lê, An\",\"say \"\"hi\"\"\",\"'=HYPERLINK(\"\"x\"\")\",,0901\r\n");
        csv.ToBytes()[..3].ShouldBe(new byte[] { 0xEF, 0xBB, 0xBF });
    }
}

public class TemporaryPasswordTests
{
    /// <summary>
    /// Every generated temporary password passes the password policy. A plain draw from a mixed alphabet missed a digit
    /// about one time in nine, which made provisioning a center fail at random (identity.bootstrap_invalid).
    /// </summary>
    [Fact]
    public void Temporary_passwords_always_meet_the_policy()
    {
        for (var i = 0; i < 2000; i++)
        {
            Konnyaku.Identity.Domain.PasswordPolicy.Check(Konnyaku.Identity.Application.AccountAdminHandler.TemporaryPassword()).ShouldBeNull();
        }
    }
}
