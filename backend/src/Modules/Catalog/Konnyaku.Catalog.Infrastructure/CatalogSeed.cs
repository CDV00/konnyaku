using Konnyaku.SharedKernel;

namespace Konnyaku.Catalog.Infrastructure;

/// <summary>
/// Initial reference data. CEFR equivalences are commonly cited approximations, stored only for comparison
/// across languages — never used for grading.
/// </summary>
internal static class CatalogSeed
{
    private static readonly (string Code, string Vi, string En)[] FourSkills =
    [
        ("LISTENING", "Nghe", "Listening"), ("READING", "Đọc", "Reading"), ("SPEAKING", "Nói", "Speaking"), ("WRITING", "Viết", "Writing"),
    ];

    private static LocalizedString L(string vi, string en) => new(new Dictionary<string, string> { ["vi"] = vi, ["en"] = en });

    public static readonly object[] Languages =
    [
        new { Code = "en", Name = L("Tiếng Anh", "English") },
        new { Code = "ja", Name = L("Tiếng Nhật", "Japanese") },
        new { Code = "zh-Hans", Name = L("Tiếng Trung (giản thể)", "Chinese (Simplified)") },
        new { Code = "ko", Name = L("Tiếng Hàn", "Korean") },
        new { Code = "fr", Name = L("Tiếng Pháp", "French") },
        new { Code = "de", Name = L("Tiếng Đức", "German") },
        new { Code = "vi", Name = L("Tiếng Việt", "Vietnamese") },
    ];

    public static readonly object[] Frameworks =
    [
        new { Code = "CEFR", Name = L("Khung tham chiếu châu Âu (CEFR)", "Common European Framework (CEFR)"), LanguageCode = (string?)null },
        new { Code = "IELTS", Name = L("IELTS", "IELTS"), LanguageCode = (string?)"en" },
        new { Code = "CAMBRIDGE_YLE", Name = L("Cambridge cho trẻ em (YLE)", "Cambridge Young Learners (YLE)"), LanguageCode = (string?)"en" },
        new { Code = "JLPT", Name = L("Năng lực tiếng Nhật (JLPT)", "Japanese-Language Proficiency Test (JLPT)"), LanguageCode = (string?)"ja" },
        new { Code = "HSK", Name = L("Năng lực Hán ngữ (HSK)", "Chinese Proficiency Test (HSK)"), LanguageCode = (string?)"zh-Hans" },
        new { Code = "TOPIK", Name = L("Năng lực tiếng Hàn (TOPIK)", "Test of Proficiency in Korean (TOPIK)"), LanguageCode = (string?)"ko" },
    ];

    public static readonly object[] Levels =
    [
        .. Range("CEFR", ["A1", "A2", "B1", "B2", "C1", "C2"], ["A1", "A2", "B1", "B2", "C1", "C2"]),
        .. Range("IELTS", ["4.0", "4.5", "5.0", "5.5", "6.0", "6.5", "7.0", "7.5", "8.0", "8.5", "9.0"],
            ["B1", "B1", "B1", "B2", "B2", "B2", "C1", "C1", "C1", "C2", "C2"]),
        .. Range("CAMBRIDGE_YLE", ["STARTERS", "MOVERS", "FLYERS"], ["PreA1", "A1", "A2"]),
        .. Range("JLPT", ["N5", "N4", "N3", "N2", "N1"], ["A1", "A2", "B1", "B2", "C1"]),
        .. Range("HSK", ["HSK1", "HSK2", "HSK3", "HSK4", "HSK5", "HSK6"], [null, null, null, null, null, null]),
        .. Range("TOPIK", ["TOPIK1", "TOPIK2", "TOPIK3", "TOPIK4", "TOPIK5", "TOPIK6"], [null, null, null, null, null, null]),
    ];

    public static readonly object[] Skills =
    [
        .. Skill("CEFR", FourSkills),
        .. Skill("IELTS", FourSkills),
        .. Skill("CAMBRIDGE_YLE", [("LISTENING", "Nghe", "Listening"), ("READING_WRITING", "Đọc & Viết", "Reading & Writing"), ("SPEAKING", "Nói", "Speaking")]),
        .. Skill("JLPT", [("VOCABULARY_KANJI", "Từ vựng & Kanji", "Vocabulary & Kanji"), ("GRAMMAR", "Ngữ pháp", "Grammar"), ("READING", "Đọc", "Reading"), ("LISTENING", "Nghe", "Listening")]),
        .. Skill("HSK", [("LISTENING", "Nghe", "Listening"), ("READING", "Đọc", "Reading"), ("WRITING", "Viết", "Writing"), ("CHARACTERS", "Chữ Hán", "Characters")]),
        .. Skill("TOPIK", [("LISTENING", "Nghe", "Listening"), ("READING", "Đọc", "Reading"), ("WRITING", "Viết", "Writing")]),
    ];

    private static IEnumerable<object> Range(string framework, string[] codes, string?[] cefr) =>
        codes.Select((code, i) => (object)new { FrameworkCode = framework, Code = code, Ordinal = i + 1, CefrApprox = cefr[i] });

    private static IEnumerable<object> Skill(string framework, (string Code, string Vi, string En)[] skills) =>
        skills.Select(s => (object)new { FrameworkCode = framework, s.Code, Name = L(s.Vi, s.En) });
}
