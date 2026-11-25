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
    public TenantStatus Status { get; private set; } = TenantStatus.Active;
    public DateTimeOffset? SuspendedAt { get; private set; }
    public string? SuspendedReason { get; private set; }

    /// <summary>Sub-domains of the base domain the platform keeps for itself (admin console, API, mail).</summary>
    public static readonly IReadOnlySet<string> ReservedSlugs = new HashSet<string>
    {
        "admin", "api", "app", "platform", "www", "mail", "smtp", "static", "assets", "cdn", "status", "help", "docs",
    };

    public static Result<Tenant> Create(string slug, string name, string defaultLocale, IReadOnlyCollection<string> enabledLocales, string timeZone)
    {
        var normalizedSlug = (slug ?? string.Empty).Trim().ToLowerInvariant();
        if (!SlugPattern().IsMatch(normalizedSlug))
        {
            return Error.Validation("platform.slug_invalid");
        }

        if (ReservedSlugs.Contains(normalizedSlug))
        {
            return Error.Validation("platform.slug_reserved", new Dictionary<string, object?> { ["slug"] = normalizedSlug });
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

    /// <summary>Stops sign-in and every API call of the tenant; the public site shows a notice. Data is kept.</summary>
    public Result<bool> Suspend(string? reason, DateTimeOffset now)
    {
        if (Status == TenantStatus.Suspended)
        {
            return Error.Conflict("platform.tenant_already_suspended");
        }

        Status = TenantStatus.Suspended;
        SuspendedAt = now;
        SuspendedReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        return true;
    }

    public Result<bool> Resume()
    {
        if (Status != TenantStatus.Suspended)
        {
            return Error.Conflict("platform.tenant_not_suspended");
        }

        Status = TenantStatus.Active;
        SuspendedAt = null;
        SuspendedReason = null;
        return true;
    }

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{1,38}[a-z0-9])$")]
    private static partial Regex SlugPattern();
}

public enum TenantStatus
{
    Active,
    Suspended,
}

/// <summary>Host name that resolves to a tenant ({slug}.{base domain} or a custom domain).</summary>
public sealed partial class TenantDomain : Entity
{
    private TenantDomain()
    {
    }

    public TenantDomain(Guid tenantId, string host)
    {
        TenantId = tenantId;
        Host = NormalizeHost(host);
    }

    public static string NormalizeHost(string host) => host.Trim().TrimEnd('.').ToLowerInvariant();

    /// <summary>A DNS host name with at least two labels (letters, digits, hyphens).</summary>
    public static bool IsValidHost(string host) => host.Length <= 253 && HostPattern().IsMatch(host);

    public Guid TenantId { get; private set; }
    public string Host { get; private set; } = string.Empty;

    [GeneratedRegex(@"^(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex HostPattern();
}
