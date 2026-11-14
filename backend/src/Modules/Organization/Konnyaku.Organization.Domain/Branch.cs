using System.Text.RegularExpressions;
using Konnyaku.SharedKernel;

namespace Konnyaku.Organization.Domain;

/// <summary>A teaching location of a tenant — physical, or the virtual "Online" branch that owns online classes.</summary>
public sealed partial class Branch : Entity, ITenantOwned
{
    private Branch()
    {
    }

    public Guid TenantId { get; private set; }
    public string Code { get; private set; } = string.Empty;
    public LocalizedString Name { get; private set; } = null!;
    public string TimeZone { get; private set; } = string.Empty;
    public bool IsVirtual { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static Result<Branch> Create(string code, IReadOnlyDictionary<string, string> name, string timeZone, bool isVirtual)
    {
        var normalizedCode = (code ?? string.Empty).Trim().ToUpperInvariant();
        if (!CodePattern().IsMatch(normalizedCode))
        {
            return Error.Validation("organization.branch_code_invalid");
        }

        if (name is null || name.Values.All(string.IsNullOrWhiteSpace))
        {
            return Error.Validation("organization.branch_name_required");
        }

        if (string.IsNullOrWhiteSpace(timeZone) || !TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out _))
        {
            return Error.Validation("organization.time_zone_invalid", new Dictionary<string, object?> { ["timeZone"] = timeZone });
        }

        return new Branch
        {
            Code = normalizedCode,
            Name = new LocalizedString(name),
            TimeZone = timeZone,
            IsVirtual = isVirtual,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    [GeneratedRegex("^[A-Z0-9][A-Z0-9_-]{1,19}$")]
    private static partial Regex CodePattern();
}
