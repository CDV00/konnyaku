using System.Text.Json.Serialization;
using Konnyaku.SharedKernel;

namespace Konnyaku.Assessment.Domain;

[JsonConverter(typeof(JsonStringEnumConverter<SectionItemKind>))]
public enum SectionItemKind
{
    /// <summary>One question of a bank.</summary>
    Question,

    /// <summary>Every question of a reading group, with its passage.</summary>
    Group,

    /// <summary><see cref="SectionItem.Count"/> questions drawn per attempt from a bank by tags, skill and level.</summary>
    Random,
}

public sealed class SectionItem
{
    public SectionItemKind Kind { get; set; }
    public Guid? QuestionId { get; set; }
    public Guid? GroupId { get; set; }
    public Guid? BankId { get; set; }
    public List<string> Tags { get; set; } = [];
    public string? SkillCode { get; set; }
    public string? LevelCode { get; set; }
    public int Count { get; set; } = 1;

    /// <summary>Points per question instead of the bank's.</summary>
    public decimal? Points { get; set; }

    /// <summary>Rubric of the paper used to grade essays and short answers of this item.</summary>
    public string? RubricId { get; set; }
}

/// <summary>A part of a paper, usually one skill, with its own time limit (JLPT, IELTS).</summary>
public sealed class PaperSection
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? SkillCode { get; set; }
    public int? TimeLimitMinutes { get; set; }
    public bool ShuffleQuestions { get; set; }
    public List<SectionItem> Items { get; set; } = [];
}

public sealed class PaperLayout
{
    public List<PaperSection> Sections { get; set; } = [];
    public List<Rubric> Rubrics { get; set; } = [];

    /// <summary>Sections run one after the other, each on its own clock, when they have time limits (all or none do).</summary>
    public bool Timed => Sections.Count > 0 && Sections.All(s => s.TimeLimitMinutes > 0);
}

/// <summary>A test paper ("đề"): sections of fixed or randomly drawn questions. Assigned through <see cref="Assignment"/>.</summary>
public sealed class Paper : Entity, ITenantOwned
{
    public const int MaxSections = 10;
    public const int MaxItemsPerSection = 200;

    private Paper()
    {
    }

    public Guid TenantId { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string LanguageCode { get; private set; } = string.Empty;
    public string? FrameworkCode { get; private set; }
    public string? Description { get; private set; }

    /// <summary>Show furigana / pinyin (<c>&lt;rt&gt;</c>) while taking the test; JLPT papers hide them.</summary>
    public bool ShowRuby { get; private set; } = true;

    public PaperLayout Layout { get; private set; } = new();
    public Guid OwnerId { get; private set; }
    public bool Archived { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static Result<Paper> Create(string title, string languageCode, string? frameworkCode, string? description, bool showRuby, PaperLayout layout,
        Guid ownerId, DateTimeOffset now)
    {
        var paper = new Paper { OwnerId = ownerId, CreatedAt = now };
        var updated = paper.Update(title, languageCode, frameworkCode, description, showRuby, layout, now);
        return updated.IsSuccess ? paper : updated.Error!;
    }

    public Result<bool> Update(string title, string languageCode, string? frameworkCode, string? description, bool showRuby, PaperLayout layout,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return Error.Validation("assessment.title_required");
        }

        if (Validate(layout) is { } error)
        {
            return error;
        }

        Title = title.Trim();
        LanguageCode = languageCode.Trim();
        FrameworkCode = string.IsNullOrWhiteSpace(frameworkCode) ? null : frameworkCode;
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        ShowRuby = showRuby;
        Layout = layout;
        UpdatedAt = now;
        return true;
    }

    public void SetArchived(bool archived, DateTimeOffset now)
    {
        Archived = archived;
        UpdatedAt = now;
    }

    private static Error? Validate(PaperLayout layout)
    {
        if (layout.Sections.Count == 0 || layout.Sections.All(s => s.Items.Count == 0))
        {
            return Error.Validation("assessment.paper_empty");
        }

        if (layout.Sections.Count > MaxSections || layout.Sections.Any(s => s.Items.Count > MaxItemsPerSection))
        {
            return Error.Validation("assessment.paper_too_large");
        }

        if (layout.Sections.Any(s => s.TimeLimitMinutes is <= 0 or > 600) ||
            (layout.Sections.Any(s => s.TimeLimitMinutes is not null) && !layout.Timed))
        {
            return Error.Validation("assessment.section_time_limits_mixed");
        }

        var rubricIds = new HashSet<string>();
        foreach (var rubric in layout.Rubrics)
        {
            if (string.IsNullOrWhiteSpace(rubric.Id) || !rubricIds.Add(rubric.Id) || string.IsNullOrWhiteSpace(rubric.Name) ||
                rubric.Criteria.Count is 0 or > 10 || rubric.Criteria.Any(c => string.IsNullOrWhiteSpace(c.Id) || c.MaxBand is <= 0 or > 100) ||
                rubric.Criteria.Select(c => c.Id).Distinct().Count() != rubric.Criteria.Count)
            {
                return Error.Validation("assessment.rubric_invalid");
            }
        }

        foreach (var item in layout.Sections.SelectMany(s => s.Items))
        {
            var ok = item.Kind switch
            {
                SectionItemKind.Question => item.QuestionId is not null,
                SectionItemKind.Group => item.GroupId is not null,
                SectionItemKind.Random => item.BankId is not null && item.Count is > 0 and <= 100,
                _ => false,
            };
            if (!ok || item.Points is <= 0 or > 1000 || (item.RubricId is not null && !rubricIds.Contains(item.RubricId)))
            {
                return Error.Validation("assessment.section_item_invalid");
            }
        }

        return null;
    }
}
