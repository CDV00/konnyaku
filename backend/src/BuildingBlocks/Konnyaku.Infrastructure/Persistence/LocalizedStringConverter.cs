using System.Text.Json;
using Konnyaku.SharedKernel;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Konnyaku.Infrastructure.Persistence;

/// <summary>Stores <see cref="LocalizedString"/> as a jsonb object keyed by BCP 47 locale.</summary>
public sealed class LocalizedStringConverter() : ValueConverter<LocalizedString, string>(v => ToJson(v), s => FromJson(s))
{
    public static readonly ValueComparer<LocalizedString> Comparer = new(
        (a, b) => ToJson(a!) == ToJson(b!),
        v => ToJson(v).GetHashCode(StringComparison.Ordinal),
        v => FromJson(ToJson(v)));

    public static string ToJson(LocalizedString value) => JsonSerializer.Serialize(value.Values);

    public static LocalizedString FromJson(string json) =>
        new(JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? []);
}
