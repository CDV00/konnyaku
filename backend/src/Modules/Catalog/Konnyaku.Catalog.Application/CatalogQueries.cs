using Konnyaku.Catalog.Contracts;
using Konnyaku.Catalog.Domain;
using Microsoft.EntityFrameworkCore;

namespace Konnyaku.Catalog.Application;

public interface ICatalogDbContext
{
    DbSet<Language> Languages { get; }
    DbSet<ProficiencyFramework> Frameworks { get; }
}

public sealed class CatalogQueries(ICatalogDbContext db) : ICatalogApi
{
    public async Task<IReadOnlyList<LanguageDto>> ListLanguagesAsync(CancellationToken cancellationToken)
    {
        var languages = await db.Languages.AsNoTracking().OrderBy(l => l.Code).ToListAsync(cancellationToken);
        return languages.Select(l => new LanguageDto(l.Code, l.Name.Values)).ToList();
    }

    public async Task<IReadOnlyList<FrameworkDto>> ListFrameworksAsync(string? languageCode, CancellationToken cancellationToken)
    {
        var query = db.Frameworks.AsNoTracking().Include(f => f.Levels).Include(f => f.Skills).AsQueryable();
        if (!string.IsNullOrWhiteSpace(languageCode))
        {
            query = query.Where(f => f.LanguageCode == null || f.LanguageCode == languageCode);
        }

        var frameworks = await query.OrderBy(f => f.Code).ToListAsync(cancellationToken);
        return frameworks.Select(ToDto).ToList();
    }

    public async Task<FrameworkDto?> GetFrameworkAsync(string code, CancellationToken cancellationToken)
    {
        var framework = await db.Frameworks.AsNoTracking().Include(f => f.Levels).Include(f => f.Skills)
            .FirstOrDefaultAsync(f => f.Code == code, cancellationToken);
        return framework is null ? null : ToDto(framework);
    }

    private static FrameworkDto ToDto(ProficiencyFramework f) => new(
        f.Code, f.Name.Values, f.LanguageCode,
        f.Levels.OrderBy(l => l.Ordinal).Select(l => new LevelDto(l.Code, l.Ordinal, l.CefrApprox)).ToList(),
        f.Skills.OrderBy(s => s.Code).Select(s => new SkillDto(s.Code, s.Name.Values)).ToList());
}
