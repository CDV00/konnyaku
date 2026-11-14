using Konnyaku.SharedKernel;

namespace Konnyaku.Catalog.Domain;

/// <summary>Teaching language, identified by a BCP 47 code (en, ja, zh-Hans…).</summary>
public sealed class Language
{
    private Language()
    {
    }

    public string Code { get; private set; } = string.Empty;
    public LocalizedString Name { get; private set; } = null!;
}

/// <summary>
/// A proficiency framework (CEFR, JLPT, HSK, TOPIK, IELTS…). <see cref="LanguageCode"/> is null for frameworks
/// shared by several languages (CEFR is used for English, French, German…).
/// </summary>
public sealed class ProficiencyFramework
{
    private ProficiencyFramework()
    {
    }

    public string Code { get; private set; } = string.Empty;
    public LocalizedString Name { get; private set; } = null!;
    public string? LanguageCode { get; private set; }
    public List<ProficiencyLevel> Levels { get; private set; } = [];
    public List<SkillDimension> Skills { get; private set; } = [];
}

/// <summary>A level of a framework. <see cref="CefrApprox"/> is only a reference for cross-language comparison.</summary>
public sealed class ProficiencyLevel
{
    private ProficiencyLevel()
    {
    }

    public string FrameworkCode { get; private set; } = string.Empty;
    public string Code { get; private set; } = string.Empty;
    public int Ordinal { get; private set; }
    public string? CefrApprox { get; private set; }
}

public sealed class SkillDimension
{
    private SkillDimension()
    {
    }

    public string FrameworkCode { get; private set; } = string.Empty;
    public string Code { get; private set; } = string.Empty;
    public LocalizedString Name { get; private set; } = null!;
}
