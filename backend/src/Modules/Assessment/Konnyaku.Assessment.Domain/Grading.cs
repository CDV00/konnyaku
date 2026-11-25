using System.Globalization;
using System.Text;

namespace Konnyaku.Assessment.Domain;

/// <summary>Outcome of machine grading one item: a score, or "a teacher must decide".</summary>
public readonly record struct AutoGrade(decimal? Score, bool NeedsTeacher)
{
    public static AutoGrade Scored(decimal score) => new(score, false);

    public static readonly AutoGrade Teacher = new(null, true);
}

/// <summary>
/// Machine grading per question type. Choice, true/false and ordering are all-or-nothing; blanks and matching give
/// partial credit per blank / pair; short answers matching an accepted answer get full points, others go to the teacher;
/// essays always go to the teacher.
/// </summary>
public static class AutoGrader
{
    public static AutoGrade Grade(QuestionType type, AnswerKey key, ItemResponse? response, decimal points)
    {
        response ??= new ItemResponse();
        switch (type)
        {
            case QuestionType.SingleChoice:
            case QuestionType.TrueFalse:
                return AutoGrade.Scored(response.Choices.Count == 1 && key.Choices.Count == 1 && response.Choices[0] == key.Choices[0] ? points : 0);
            case QuestionType.MultipleChoice:
                return AutoGrade.Scored(response.Choices.ToHashSet().SetEquals(key.Choices) ? points : 0);
            case QuestionType.Ordering:
                return AutoGrade.Scored(response.Order.SequenceEqual(key.Order) ? points : 0);
            case QuestionType.Matching:
                if (key.Pairs.Count == 0)
                {
                    return AutoGrade.Scored(0);
                }

                var matched = key.Pairs.Count(p => response.Pairs.TryGetValue(p.Key, out var target) && target == p.Value);
                return AutoGrade.Scored(Share(points, matched, key.Pairs.Count));
            case QuestionType.FillBlank:
                if (key.Blanks.Count == 0)
                {
                    return AutoGrade.Scored(0);
                }

                var correct = key.Blanks.Select((accepted, i) => i < response.Blanks.Count && AnswerMatcher.Matches(response.Blanks[i], accepted, key))
                    .Count(ok => ok);
                return AutoGrade.Scored(Share(points, correct, key.Blanks.Count));
            case QuestionType.ShortAnswer:
                if (string.IsNullOrWhiteSpace(response.Text))
                {
                    return AutoGrade.Scored(0);
                }

                return AnswerMatcher.Matches(response.Text, key.Accepted, key) ? AutoGrade.Scored(points) : AutoGrade.Teacher;
            case QuestionType.Essay:
                return string.IsNullOrWhiteSpace(response.Text) ? AutoGrade.Scored(0) : AutoGrade.Teacher;
            default:
                return AutoGrade.Teacher;
        }
    }

    /// <summary>Points for <paramref name="part"/> of <paramref name="whole"/>, rounded to 2 decimals.</summary>
    public static decimal Share(decimal points, int part, int whole) => whole == 0 ? 0 : Math.Round(points * part / whole, 2);
}

/// <summary>Compares a typed answer with accepted answers.</summary>
public static class AnswerMatcher
{
    public static bool Matches(string? answer, IEnumerable<string> accepted, AnswerKey options)
    {
        if (string.IsNullOrWhiteSpace(answer))
        {
            return false;
        }

        var given = Normalize(answer, options.IgnoreCase, options.IgnoreDiacritics);
        return accepted.Any(a => !string.IsNullOrWhiteSpace(a) && Normalize(a, options.IgnoreCase, options.IgnoreDiacritics) == given);
    }

    /// <summary>
    /// Width-folds (NFKC: full-width "ＡＢＣ" → "ABC", half-width kana → kana), trims and collapses spaces; optionally folds
    /// case and removes Latin / Vietnamese diacritics. Kana voicing marks (が, ぱ) are never removed.
    /// </summary>
    public static string Normalize(string value, bool ignoreCase, bool ignoreDiacritics)
    {
        var text = value.Normalize(NormalizationForm.FormKC).Trim();
        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (ignoreDiacritics)
        {
            var decomposed = text.Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(decomposed.Length);
            foreach (var ch in decomposed)
            {
                // Combining Diacritical Marks block only: kana voicing marks (U+3099, U+309A) stay.
                if (ch is >= '̀' and <= 'ͯ')
                {
                    continue;
                }

                builder.Append(ch switch { 'đ' => 'd', 'Đ' => 'D', _ => ch });
            }

            text = builder.ToString().Normalize(NormalizationForm.FormC);
        }

        return ignoreCase ? text.ToLower(CultureInfo.InvariantCulture) : text;
    }

    /// <summary>Words (space-separated) or characters (letters only, for Japanese and Chinese) of an essay.</summary>
    public static int Count(string? text, CountMode mode)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        return mode == CountMode.Characters
            ? text.Count(ch => !char.IsWhiteSpace(ch) && !char.IsPunctuation(ch) && !char.IsSymbol(ch))
            : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    }
}

/// <summary>A rubric criterion scored on a band (IELTS Writing: 4 criteria × band 0–9).</summary>
public sealed class RubricCriterion
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int MaxBand { get; set; } = 9;
}

public sealed class Rubric
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public List<RubricCriterion> Criteria { get; set; } = [];

    /// <summary>Item points from the bands given: points × Σbands / ΣmaxBands.</summary>
    public decimal? Score(IReadOnlyDictionary<string, decimal> bands, decimal points)
    {
        if (Criteria.Count == 0 || Criteria.Any(c => !bands.TryGetValue(c.Id, out var b) || b < 0 || b > c.MaxBand))
        {
            return null;
        }

        var max = Criteria.Sum(c => c.MaxBand);
        return max == 0 ? 0 : Math.Round(points * Criteria.Sum(c => bands[c.Id]) / max, 2);
    }
}
