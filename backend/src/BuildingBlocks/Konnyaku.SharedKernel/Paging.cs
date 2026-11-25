namespace Konnyaku.SharedKernel;

/// <summary>
/// Paging of every list API: <c>?page=1&amp;pageSize=25&amp;sort=name&amp;dir=desc</c>. Pages are numbered from 1 and
/// sizes are capped, so no request reads a whole table. Lists of a center hold thousands of rows, not millions, and
/// staff jump between numbered pages of a sorted, filtered list: offset paging (with a unique tie-breaker in the
/// ORDER BY so pages never overlap) fits better than cursors there.
/// </summary>
public sealed record PageRequest(int Page, int PageSize, string? Sort, bool Descending)
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 200;

    public int Skip => (Page - 1) * PageSize;

    public static PageRequest Of(int? page, int? pageSize, string? sort = null, string? dir = null) => new(
        Math.Max(1, page ?? 1),
        Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize),
        string.IsNullOrWhiteSpace(sort) ? null : sort.Trim(),
        string.Equals(dir, "desc", StringComparison.OrdinalIgnoreCase));
}

/// <summary>One page of a list and the number of rows matching the filters (all pages together).</summary>
public sealed record Page<T>(IReadOnlyList<T> Items, int Total, int PageNumber, int PageSize)
{
    public Page<TOut> Map<TOut>(Func<T, TOut> map) => new(Items.Select(map).ToList(), Total, PageNumber, PageSize);

    public static Page<T> Empty(PageRequest request) => new([], 0, request.Page, request.PageSize);

    /// <summary>Pages a list already in memory (small lists such as staff members).</summary>
    public static Page<T> FromList(IReadOnlyList<T> all, PageRequest request) =>
        new(all.Skip(request.Skip).Take(request.PageSize).ToList(), all.Count, request.Page, request.PageSize);
}

/// <summary>
/// Search terms the way the database compares them: lower case, without Vietnamese (or other) diacritics, "đ" as "d"
/// — the same folding as <c>public.kk_unaccent(lower(...))</c> on the stored side, so "an" finds "Ân" and the reverse.
/// </summary>
public static class TextSearch
{
    public static string Fold(string value)
    {
        var decomposed = value.Trim().ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
        var folded = new System.Text.StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            folded.Append(c == 'đ' ? 'd' : c);
        }

        return folded.ToString().Normalize(System.Text.NormalizationForm.FormC);
    }

    /// <summary>
    /// The folded term of a search box, or null when empty. A phone number typed with spaces, dots or +84 becomes the
    /// digits it is stored as ("+84 90 123" → "090123").
    /// </summary>
    public static string? Term(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var trimmed = query.Trim();
        if (trimmed.Length > 100)
        {
            trimmed = trimmed[..100];
        }

        if (trimmed.All(c => char.IsDigit(c) || c is ' ' or '.' or '-' or '+' or '(' or ')') && trimmed.Count(char.IsDigit) >= 3)
        {
            var digits = new string(trimmed.Where(c => char.IsDigit(c) || c == '+').ToArray());
            return digits.StartsWith("+84", StringComparison.Ordinal) ? "0" + digits[3..] : digits.Replace("+", string.Empty);
        }

        return Fold(trimmed);
    }

    /// <summary><c>%term%</c> for LIKE, with the LIKE wildcards of the term escaped.</summary>
    public static string Contains(string term) =>
        "%" + term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
}
