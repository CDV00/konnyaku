using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Konnyaku.Infrastructure.Persistence;

/// <summary>Stores a small value (dictionary, list) as jsonb through explicit serialization.</summary>
public sealed class JsonValueConverter<T>() : ValueConverter<T, string>(v => Serialize(v), s => Deserialize(s))
    where T : class, new()
{
    public static readonly ValueComparer<T> Comparer = new(
        (a, b) => Serialize(a!) == Serialize(b!),
        v => Serialize(v).GetHashCode(StringComparison.Ordinal),
        v => Deserialize(Serialize(v)));

    public static string Serialize(T value) => JsonSerializer.Serialize(value);

    public static T Deserialize(string json) => JsonSerializer.Deserialize<T>(json) ?? new T();
}

public static class JsonPropertyExtensions
{
    public static PropertyBuilder<T> HasJsonConversion<T>(this PropertyBuilder<T> property) where T : class, new() =>
        property.HasConversion(new JsonValueConverter<T>(), JsonValueConverter<T>.Comparer).HasColumnType("jsonb");
}
