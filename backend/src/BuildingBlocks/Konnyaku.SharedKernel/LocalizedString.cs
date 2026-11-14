namespace Konnyaku.SharedKernel;

/// <summary>Display text entered by a tenant in several locales (BCP 47 keys), e.g. {"vi": "...", "en": "..."}.</summary>
public sealed class LocalizedString
{
    private readonly Dictionary<string, string> _values;

    public LocalizedString(IReadOnlyDictionary<string, string> values)
    {
        if (values.Count == 0)
        {
            throw new ArgumentException("At least one locale is required.", nameof(values));
        }

        _values = values
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
            .ToDictionary(kv => kv.Key.Trim(), kv => kv.Value.Trim(), StringComparer.OrdinalIgnoreCase);

        if (_values.Count == 0)
        {
            throw new ArgumentException("At least one non-empty value is required.", nameof(values));
        }
    }

    public IReadOnlyDictionary<string, string> Values => _values;

    /// <summary>Exact locale, then its language (zh-Hans → zh), then the fallback locale, then any value.</summary>
    public string Get(string locale, string fallbackLocale)
    {
        if (_values.TryGetValue(locale, out var exact))
        {
            return exact;
        }

        var dash = locale.IndexOf('-');
        if (dash > 0 && _values.TryGetValue(locale[..dash], out var language))
        {
            return language;
        }

        return _values.TryGetValue(fallbackLocale, out var fallback) ? fallback : _values.Values.First();
    }
}
