using System.Text.Json.Serialization;
using Konnyaku.SharedKernel;

namespace Konnyaku.Assessment.Domain;

/// <summary>
/// Question formats of the core engine. Listening and speaking (media, recording) come with the file store; they will be
/// new values here, graded through the same <see cref="AutoGrader"/> / teacher queue.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<QuestionType>))]
public enum QuestionType
{
    SingleChoice,
    MultipleChoice,
    TrueFalse,

    /// <summary>Blanks are written <c>[[1]]</c>, <c>[[2]]</c>… in the stem; each has its own list of accepted answers.</summary>
    FillBlank,
    Matching,
    Ordering,

    /// <summary>Auto-matched against the accepted answers; a mismatch goes to the teacher instead of scoring zero.</summary>
    ShortAnswer,
    Essay,
}

/// <summary>Who may see a bank: its owner and the assessment managers, or every author of the center.</summary>
public enum BankVisibility
{
    Private,
    Shared,
}

/// <summary>A labelled option: a choice, an item to order, the left (<c>Options</c>) or right (<c>Targets</c>) side of a match.</summary>
public sealed class ChoiceOption
{
    public string Id { get; set; } = string.Empty;
    public string Html { get; set; } = string.Empty;
}

[JsonConverter(typeof(JsonStringEnumConverter<CountMode>))]
public enum CountMode
{
    Words,

    /// <summary>Japanese and Chinese have no spaces: limits count characters.</summary>
    Characters,
}

public sealed class EssayLimits
{
    public int? Min { get; set; }
    public int? Max { get; set; }
    public CountMode Count { get; set; } = CountMode.Words;
}

/// <summary>What the learner sees besides the stem (never the answers).</summary>
public sealed class QuestionContent
{
    public List<ChoiceOption> Options { get; set; } = [];
    public List<ChoiceOption> Targets { get; set; } = [];
    public int Blanks { get; set; }
    public EssayLimits? Essay { get; set; }

    /// <summary>Shuffle options (and match targets) per attempt. Ordering items are always shuffled.</summary>
    public bool Shuffle { get; set; } = true;
}

/// <summary>The correct answers and how typed answers are compared.</summary>
public sealed class AnswerKey
{
    /// <summary>Correct option ids (single, multiple choice); <c>true</c> or <c>false</c> for true/false.</summary>
    public List<string> Choices { get; set; } = [];

    /// <summary>Option ids in the correct order.</summary>
    public List<string> Order { get; set; } = [];

    /// <summary>Option id → target id.</summary>
    public Dictionary<string, string> Pairs { get; set; } = [];

    /// <summary>Accepted answers per blank, in blank order. Kana and kanji spellings are listed explicitly.</summary>
    public List<List<string>> Blanks { get; set; } = [];

    /// <summary>Accepted answers of a short answer question.</summary>
    public List<string> Accepted { get; set; } = [];

    public bool IgnoreCase { get; set; } = true;

    /// <summary>"Hà Nội" matches "ha noi" (Latin and Vietnamese marks only; kana voicing marks are kept).</summary>
    public bool IgnoreDiacritics { get; set; }
}

/// <summary>A learner's answer to one item (only the fields of its type are used).</summary>
public sealed class ItemResponse
{
    public List<string> Choices { get; set; } = [];
    public List<string> Order { get; set; } = [];
    public Dictionary<string, string> Pairs { get; set; } = [];
    public List<string> Blanks { get; set; } = [];
    public string? Text { get; set; }

    public bool IsEmpty =>
        Choices.Count == 0 && Order.Count == 0 && Pairs.Count == 0 && Blanks.All(string.IsNullOrWhiteSpace) && string.IsNullOrWhiteSpace(Text);
}

/// <summary>Bank of questions of a center, per language (and optionally program).</summary>
public sealed class QuestionBank : Entity, ITenantOwned
{
    private QuestionBank()
    {
    }

    public QuestionBank(string name, string languageCode, Guid? programId, BankVisibility visibility, string? description, Guid ownerId, DateTimeOffset now)
    {
        OwnerId = ownerId;
        CreatedAt = now;
        Update(name, languageCode, programId, visibility, description, now);
    }

    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string LanguageCode { get; private set; } = string.Empty;
    public Guid? ProgramId { get; private set; }
    public BankVisibility Visibility { get; private set; }
    public string? Description { get; private set; }
    public Guid OwnerId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public void Update(string name, string languageCode, Guid? programId, BankVisibility visibility, string? description, DateTimeOffset now)
    {
        Name = name.Trim();
        LanguageCode = languageCode.Trim();
        ProgramId = programId;
        Visibility = visibility;
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        UpdatedAt = now;
    }
}

/// <summary>Questions sharing one reading passage (later also one listening file).</summary>
public sealed class QuestionGroup : Entity, ITenantOwned
{
    private QuestionGroup()
    {
    }

    public QuestionGroup(Guid bankId, string title, string passage, DateTimeOffset now)
    {
        BankId = bankId;
        Update(title, passage, now);
    }

    public Guid TenantId { get; private set; }
    public Guid BankId { get; private set; }
    public string Title { get; private set; } = string.Empty;

    /// <summary>Sanitized rich text.</summary>
    public string Passage { get; private set; } = string.Empty;

    /// <summary>Reserved for the file store (audio of a listening group).</summary>
    public List<Guid> Media { get; private set; } = [];

    public DateTimeOffset UpdatedAt { get; private set; }

    public void Update(string title, string passage, DateTimeOffset now)
    {
        Title = title.Trim();
        Passage = passage;
        UpdatedAt = now;
    }
}

public sealed record QuestionData(
    QuestionType Type, string Stem, QuestionContent Content, AnswerKey Key, decimal Points, string? FrameworkCode, string? SkillCode,
    string? LevelCode, IReadOnlyList<string> Tags, string? Explanation, Guid? GroupId);

/// <summary>
/// A question. Every edit raises <see cref="Version"/>; attempts keep a snapshot of the version they were given, so past
/// results never change.
/// </summary>
public sealed class Question : Entity, ITenantOwned
{
    public const int MaxOptions = 12;

    private Question()
    {
    }

    public Guid TenantId { get; private set; }
    public Guid BankId { get; private set; }
    public Guid? GroupId { get; private set; }
    public QuestionType Type { get; private set; }

    /// <summary>Sanitized rich text (furigana and pinyin as <c>&lt;ruby&gt;</c>).</summary>
    public string Stem { get; private set; } = string.Empty;

    /// <summary>Reserved for the file store (images, audio): ids of stored files.</summary>
    public List<Guid> Media { get; private set; } = [];

    public QuestionContent Content { get; private set; } = new();
    public AnswerKey Key { get; private set; } = new();
    public decimal Points { get; private set; }

    /// <summary>Catalog <c>SkillDimension</c> (framework code + skill code) and <c>ProficiencyLevel</c> of the same framework.</summary>
    public string? FrameworkCode { get; private set; }

    public string? SkillCode { get; private set; }
    public string? LevelCode { get; private set; }
    public List<string> Tags { get; private set; } = [];
    public string? Explanation { get; private set; }
    public int Version { get; private set; }
    public bool Archived { get; private set; }
    public Guid AuthorId { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static Result<Question> Create(Guid bankId, QuestionData data, Guid authorId, DateTimeOffset now)
    {
        var question = new Question { BankId = bankId, AuthorId = authorId };
        var applied = question.Apply(data, now);
        return applied.IsSuccess ? question : applied.Error!;
    }

    public Result<bool> Update(QuestionData data, DateTimeOffset now) => Apply(data, now);

    public void Archive(DateTimeOffset now)
    {
        Archived = true;
        UpdatedAt = now;
    }

    public void Restore(DateTimeOffset now)
    {
        Archived = false;
        UpdatedAt = now;
    }

    private Result<bool> Apply(QuestionData data, DateTimeOffset now)
    {
        if (QuestionRules.Validate(data) is { } error)
        {
            return error;
        }

        Type = data.Type;
        Stem = data.Stem;
        Content = QuestionRules.Normalize(data.Type, data.Content);
        if (Type == QuestionType.FillBlank)
        {
            Content.Blanks = QuestionRules.BlankMarkers(Stem);
        }

        Key = data.Key;
        Points = data.Points;
        FrameworkCode = data.SkillCode is null && data.LevelCode is null ? null : data.FrameworkCode;
        SkillCode = data.SkillCode;
        LevelCode = data.LevelCode;
        Tags = data.Tags.Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length > 0).Distinct().Take(20).ToList();
        Explanation = string.IsNullOrWhiteSpace(data.Explanation) ? null : data.Explanation;
        GroupId = data.GroupId;
        Version++;
        UpdatedAt = now;
        return true;
    }
}

/// <summary>Shape rules per question type. Error codes are <c>assessment.*</c>.</summary>
public static class QuestionRules
{
    public const string TrueOption = "true";
    public const string FalseOption = "false";

    public static Error? Validate(QuestionData d)
    {
        if (string.IsNullOrWhiteSpace(StripTags(d.Stem)))
        {
            return Error.Validation("assessment.stem_required");
        }

        if (d.Points <= 0 || d.Points > 1000)
        {
            return Error.Validation("assessment.points_invalid");
        }

        var c = d.Content;
        var k = d.Key;
        var optionIds = c.Options.Select(o => o.Id).ToList();
        bool OptionsOk(int min) =>
            c.Options.Count >= min && c.Options.Count <= Question.MaxOptions && optionIds.All(id => !string.IsNullOrWhiteSpace(id)) &&
            optionIds.Distinct().Count() == optionIds.Count && c.Options.All(o => !string.IsNullOrWhiteSpace(StripTags(o.Html)));

        switch (d.Type)
        {
            case QuestionType.SingleChoice:
                if (!OptionsOk(2))
                {
                    return Error.Validation("assessment.options_invalid");
                }

                if (k.Choices.Count != 1 || !optionIds.Contains(k.Choices[0]))
                {
                    return Error.Validation("assessment.answer_key_invalid");
                }

                break;
            case QuestionType.MultipleChoice:
                if (!OptionsOk(2))
                {
                    return Error.Validation("assessment.options_invalid");
                }

                if (k.Choices.Count == 0 || k.Choices.Distinct().Count() != k.Choices.Count || !k.Choices.All(optionIds.Contains))
                {
                    return Error.Validation("assessment.answer_key_invalid");
                }

                break;
            case QuestionType.TrueFalse:
                if (k.Choices is not [TrueOption or FalseOption])
                {
                    return Error.Validation("assessment.answer_key_invalid");
                }

                break;
            case QuestionType.FillBlank:
                var markers = BlankMarkers(d.Stem);
                if (markers == 0 || markers > 20)
                {
                    return Error.Validation("assessment.blanks_missing");
                }

                if (k.Blanks.Count != markers || k.Blanks.Any(b => b.Count == 0 || b.All(string.IsNullOrWhiteSpace)))
                {
                    return Error.Validation("assessment.answer_key_invalid");
                }

                break;
            case QuestionType.Matching:
                var targetIds = c.Targets.Select(t => t.Id).ToList();
                if (!OptionsOk(2) || c.Targets.Count < 2 || c.Targets.Count > Question.MaxOptions ||
                    targetIds.Distinct().Count() != targetIds.Count || targetIds.Any(string.IsNullOrWhiteSpace))
                {
                    return Error.Validation("assessment.options_invalid");
                }

                if (k.Pairs.Count != c.Options.Count || !k.Pairs.Keys.All(optionIds.Contains) || !k.Pairs.Values.All(targetIds.Contains))
                {
                    return Error.Validation("assessment.answer_key_invalid");
                }

                break;
            case QuestionType.Ordering:
                if (!OptionsOk(2))
                {
                    return Error.Validation("assessment.options_invalid");
                }

                if (k.Order.Count != optionIds.Count || !k.Order.OrderBy(x => x).SequenceEqual(optionIds.OrderBy(x => x)))
                {
                    return Error.Validation("assessment.answer_key_invalid");
                }

                break;
            case QuestionType.ShortAnswer:
                if (k.Accepted.Count == 0 || k.Accepted.All(string.IsNullOrWhiteSpace))
                {
                    return Error.Validation("assessment.answer_key_invalid");
                }

                break;
            case QuestionType.Essay:
                if (c.Essay is { } e && (e.Min < 0 || e.Max <= 0 || (e.Min is { } min && e.Max is { } max && min > max)))
                {
                    return Error.Validation("assessment.essay_limits_invalid");
                }

                break;
            default:
                return Error.Validation("assessment.type_unsupported");
        }

        if ((d.SkillCode is not null || d.LevelCode is not null) && string.IsNullOrWhiteSpace(d.FrameworkCode))
        {
            return Error.Validation("assessment.framework_required");
        }

        return null;
    }

    /// <summary>Drops the parts that do not apply to the type (true/false gets its two fixed options).</summary>
    public static QuestionContent Normalize(QuestionType type, QuestionContent c) => type switch
    {
        QuestionType.SingleChoice or QuestionType.MultipleChoice or QuestionType.Ordering =>
            new QuestionContent { Options = c.Options, Shuffle = c.Shuffle },
        QuestionType.TrueFalse => new QuestionContent
        {
            Options = [new ChoiceOption { Id = TrueOption }, new ChoiceOption { Id = FalseOption }],
            Shuffle = false,
        },
        QuestionType.Matching => new QuestionContent { Options = c.Options, Targets = c.Targets, Shuffle = c.Shuffle },
        QuestionType.FillBlank => new QuestionContent { Blanks = c.Blanks, Shuffle = false },
        QuestionType.Essay => new QuestionContent { Essay = c.Essay ?? new EssayLimits(), Shuffle = false },
        _ => new QuestionContent { Shuffle = false },
    };

    /// <summary>Number of distinct <c>[[n]]</c> markers, which must be 1..n.</summary>
    public static int BlankMarkers(string stem)
    {
        var numbers = System.Text.RegularExpressions.Regex.Matches(stem, @"\[\[(\d{1,2})\]\]")
            .Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).Distinct().Order().ToList();
        return numbers.Count > 0 && numbers.SequenceEqual(Enumerable.Range(1, numbers.Count)) ? numbers.Count : 0;
    }

    public static string StripTags(string html) =>
        System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(html ?? string.Empty, "<[^>]*>", string.Empty)).Trim();
}
