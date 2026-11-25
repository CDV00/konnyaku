using System.Globalization;

namespace Konnyaku.SharedKernel;

public enum PersonalDataKind
{
    Name,
    Phone,
    Email,
    BirthYear,
}

/// <summary>
/// Marks a property holding personal data: the change log stores it masked (<see cref="PersonalDataMask"/>), and an
/// erasure request overwrites it. Lists return it masked; the full value needs a permission and is logged.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class PersonalDataAttribute(PersonalDataKind kind) : Attribute
{
    public PersonalDataKind Kind { get; } = kind;
}

/// <summary>
/// The one place personal data is masked (lists, change log, export stamps, access log). Masked values keep just enough
/// to tell records apart on a call: the start and end of a phone number, the first letter and domain of an email.
/// </summary>
public static class PersonalDataMask
{
    public static string? Mask(PersonalDataKind kind, object? value) => value switch
    {
        null => null,
        _ => kind switch
        {
            PersonalDataKind.Phone => Phone(value.ToString()),
            PersonalDataKind.Email => Email(value.ToString()),
            PersonalDataKind.BirthYear => BirthYear(value is int year ? year : int.TryParse(value.ToString(), out var y) ? y : null),
            _ => Name(value.ToString()),
        },
    };

    /// <summary><c>0901234567</c> → <c>09xx xxx 567</c>.</summary>
    public static string? Phone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
        {
            return null;
        }

        var digits = new string(phone.Where(char.IsDigit).ToArray());
        if (digits.Length < 6)
        {
            return new string('x', digits.Length);
        }

        var masked = digits[..2] + new string('x', digits.Length - 5) + digits[^3..];
        return masked.Length < 8 ? masked : $"{masked[..4]} {masked[4..^3]} {masked[^3..]}";
    }

    /// <summary><c>an.nguyen@example.com</c> → <c>a***@example.com</c>.</summary>
    public static string? Email(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return null;
        }

        var at = email.IndexOf('@', StringComparison.Ordinal);
        return at <= 0 ? "***" : $"{email[0]}***{email[at..]}";
    }

    /// <summary><c>Nguyễn Văn An</c> → <c>Nguyễn V** A**</c>: the family name and initials.</summary>
    public static string? Name(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(' ', parts.Select((part, i) => i == 0 && parts.Length > 1 ? part : StringInfo.GetNextTextElement(part) + "**"));
    }

    /// <summary><c>2014</c> → <c>201*</c>: the decade, enough to tell a child from an adult.</summary>
    public static string? BirthYear(int? year) =>
        year is { } y ? y.ToString(CultureInfo.InvariantCulture)[..^1] + "*" : null;

    /// <summary>A search term written to the access log: never the full value someone looked for.</summary>
    public static string? Term(string? term) =>
        string.IsNullOrWhiteSpace(term) ? null : term.Length <= 2 ? "**" : term[..2] + "***";
}
