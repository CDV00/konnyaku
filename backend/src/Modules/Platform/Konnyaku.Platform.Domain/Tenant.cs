using System.Text.RegularExpressions;
using Konnyaku.SharedKernel;

namespace Konnyaku.Platform.Domain;

/// <summary>A customer of the platform: one center, one legal and financial unit.</summary>
public sealed partial class Tenant : Entity
{
    /// <summary>UI locales the platform ships. A tenant enables a subset.</summary>
    public static readonly IReadOnlyList<string> SupportedLocales = ["vi", "en"];

    private Tenant()
    {
    }

    public string Slug { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string DefaultLocale { get; private set; } = "vi";
    public List<string> EnabledLocales { get; private set; } = [];
    public string TimeZone { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }

    public static Result<Tenant> Create(string slug, string name, string defaultLocale, IReadOnlyCollection<string> enabledLocales, string timeZone)
    {
        var normalizedSlug = (slug ?? string.Empty).Trim().ToLowerInvariant();
        if (!SlugPattern().IsMatch(normalizedSlug))
        {
            return Error.Validation("platform.slug_invalid");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return Error.Validation("platform.name_required");
        }

        var locales = enabledLocales.Select(l => l.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (locales.Count == 0 || locales.Any(l => !SupportedLocales.Contains(l)) || !locales.Contains(defaultLocale))
        {
            return Error.Validation("platform.locales_invalid", new Dictionary<string, object?> { ["supported"] = SupportedLocales });
        }

        if (!TimeZoneInfo.TryFindSystemTimeZoneById(timeZone ?? string.Empty, out _))
        {
            return Error.Validation("platform.time_zone_invalid");
        }

        return new Tenant
        {
            Slug = normalizedSlug,
            Name = name.Trim(),
            DefaultLocale = defaultLocale,
            EnabledLocales = locales,
            TimeZone = timeZone!,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{1,38}[a-z0-9])$")]
    private static partial Regex SlugPattern();
}

/// <summary>Host name that resolves to a tenant ({slug}.{base domain} or a custom domain).</summary>
public sealed class TenantDomain : Entity
{
    private TenantDomain()
    {
    }

    public TenantDomain(Guid tenantId, string host)
    {
        TenantId = tenantId;
        Host = host.Trim().ToLowerInvariant();
    }

    public Guid TenantId { get; private set; }
    public string Host { get; private set; } = string.Empty;
}
