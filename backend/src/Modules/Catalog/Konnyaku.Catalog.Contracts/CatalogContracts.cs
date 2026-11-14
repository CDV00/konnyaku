namespace Konnyaku.Catalog.Contracts;

public sealed record LanguageDto(string Code, IReadOnlyDictionary<string, string> Name);

public sealed record LevelDto(string Code, int Ordinal, string? CefrApprox);

public sealed record SkillDto(string Code, IReadOnlyDictionary<string, string> Name);

public sealed record FrameworkDto(
    string Code, IReadOnlyDictionary<string, string> Name, string? LanguageCode,
    IReadOnlyList<LevelDto> Levels, IReadOnlyList<SkillDto> Skills);

public interface ICatalogApi
{
    Task<IReadOnlyList<LanguageDto>> ListLanguagesAsync(CancellationToken cancellationToken);

    /// <summary>Frameworks usable for a language: its own frameworks plus shared ones (CEFR).</summary>
    Task<IReadOnlyList<FrameworkDto>> ListFrameworksAsync(string? languageCode, CancellationToken cancellationToken);

    Task<FrameworkDto?> GetFrameworkAsync(string code, CancellationToken cancellationToken);
}
