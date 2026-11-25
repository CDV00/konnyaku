using Konnyaku.Assessment.Domain;
using Konnyaku.Catalog.Contracts;
using Konnyaku.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Konnyaku.Assessment.Application;

public sealed record PaperCommand(string Title, string LanguageCode, string? FrameworkCode, string? Description, bool ShowRuby, PaperLayout? Layout);

public sealed record PaperView(
    Guid Id, string Title, string LanguageCode, string? FrameworkCode, string? Description, bool ShowRuby, PaperLayout Layout, Guid OwnerId,
    bool CanEdit, int QuestionCount, int? TotalMinutes, bool Archived, DateTimeOffset UpdatedAt);

/// <summary>A paper with the questions and groups its fixed items name (for the builder).</summary>
public sealed record PaperDetail(PaperView Paper, IReadOnlyList<QuestionView> Questions, IReadOnlyList<GroupView> Groups);

/// <summary>Questions a random item may draw from: not archived, not part of a reading group, matching every tag.</summary>
public static class QuestionPool
{
    public static IQueryable<Question> For(IQueryable<Question> questions, SectionItem item)
    {
        var pool = questions.Where(q => q.BankId == item.BankId && !q.Archived && q.GroupId == null);
        foreach (var tag in item.Tags.Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length > 0))
        {
            pool = pool.Where(q => q.Tags.Contains(tag));
        }

        if (!string.IsNullOrWhiteSpace(item.SkillCode))
        {
            pool = pool.Where(q => q.SkillCode == item.SkillCode);
        }

        if (!string.IsNullOrWhiteSpace(item.LevelCode))
        {
            pool = pool.Where(q => q.LevelCode == item.LevelCode);
        }

        return pool;
    }
}

/// <summary>Test papers: sections of fixed questions, whole reading groups and random draws.</summary>
public sealed class PaperHandlers(IAssessmentDbContext db, AssessmentAccess access, ICatalogApi catalog, TimeProvider clock)
{
    public async Task<IReadOnlyList<PaperView>> ListAsync(Actor actor, bool includeArchived, CancellationToken ct)
    {
        if (!await access.CanAuthorAsync(actor, ct))
        {
            return [];
        }

        var manager = await access.IsManagerAsync(ct);
        var papers = await db.Papers.AsNoTracking().Where(p => includeArchived || !p.Archived).OrderByDescending(p => p.UpdatedAt).Take(500).ToListAsync(ct);
        var views = new List<PaperView>();
        foreach (var paper in papers)
        {
            views.Add(await ToViewAsync(paper, manager || paper.OwnerId == actor.UserId, ct));
        }

        return views;
    }

    public async Task<Result<PaperDetail>> GetAsync(Guid id, Actor actor, CancellationToken ct)
    {
        if (!await access.CanAuthorAsync(actor, ct))
        {
            return AssessmentAccess.Forbidden;
        }

        var paper = await db.Papers.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        if (paper is null)
        {
            return Error.NotFound("assessment.paper_not_found");
        }

        var items = paper.Layout.Sections.SelectMany(s => s.Items).ToList();
        var questionIds = items.Where(i => i.QuestionId is not null).Select(i => i.QuestionId!.Value).ToArray();
        var groupIds = items.Where(i => i.GroupId is not null).Select(i => i.GroupId!.Value).ToArray();
        var questions = await db.Questions.AsNoTracking().Where(q => questionIds.Contains(q.Id) || (q.GroupId != null && groupIds.Contains(q.GroupId.Value)))
            .ToListAsync(ct);
        var groups = await db.Groups.AsNoTracking().Where(g => groupIds.Contains(g.Id)).ToListAsync(ct);
        return new PaperDetail(
            await ToViewAsync(paper, await access.CanEditAsync(paper.OwnerId, actor, ct), ct),
            questions.Select(BankHandlers.ToView).ToList(),
            groups.Select(g => new GroupView(g.Id, g.BankId, g.Title, g.Passage, questions.Count(q => q.GroupId == g.Id && !q.Archived))).ToList());
    }

    public async Task<Result<PaperView>> CreateAsync(PaperCommand command, Actor actor, CancellationToken ct)
    {
        if (!await access.CanAuthorAsync(actor, ct))
        {
            return AssessmentAccess.Forbidden;
        }

        var layout = Clean(command.Layout);
        if (await ValidateSourcesAsync(command, layout, actor, ct) is { } error)
        {
            return error;
        }

        var created = Paper.Create(command.Title, command.LanguageCode, command.FrameworkCode, command.Description, command.ShowRuby, layout,
            actor.UserId, clock.GetUtcNow());
        if (!created.IsSuccess)
        {
            return created.Error!;
        }

        db.Papers.Add(created.Value);
        await db.SaveChangesAsync(ct);
        return await ToViewAsync(created.Value, true, ct);
    }

    /// <summary>Changes apply to attempts started afterwards; started attempts keep the questions they were given.</summary>
    public async Task<Result<PaperView>> UpdateAsync(Guid id, PaperCommand command, Actor actor, CancellationToken ct)
    {
        var paper = await db.Papers.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (paper is null)
        {
            return Error.NotFound("assessment.paper_not_found");
        }

        if (!await access.CanEditAsync(paper.OwnerId, actor, ct))
        {
            return AssessmentAccess.Forbidden;
        }

        var layout = Clean(command.Layout);
        if (await ValidateSourcesAsync(command, layout, actor, ct) is { } error)
        {
            return error;
        }

        var updated = paper.Update(command.Title, command.LanguageCode, command.FrameworkCode, command.Description, command.ShowRuby, layout,
            clock.GetUtcNow());
        if (!updated.IsSuccess)
        {
            return updated.Error!;
        }

        await db.SaveChangesAsync(ct);
        return await ToViewAsync(paper, true, ct);
    }

    public async Task<Result<PaperView>> SetArchivedAsync(Guid id, bool archived, Actor actor, CancellationToken ct)
    {
        var paper = await db.Papers.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (paper is null)
        {
            return Error.NotFound("assessment.paper_not_found");
        }

        if (!await access.CanEditAsync(paper.OwnerId, actor, ct))
        {
            return AssessmentAccess.Forbidden;
        }

        paper.SetArchived(archived, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return await ToViewAsync(paper, true, ct);
    }

    /// <summary>Every fixed question and group exists in a bank the author may read; every random draw has enough questions.</summary>
    private async Task<Error?> ValidateSourcesAsync(PaperCommand command, PaperLayout layout, Actor actor, CancellationToken ct)
    {
        if ((await catalog.ListLanguagesAsync(ct)).All(l => l.Code != command.LanguageCode))
        {
            return Error.Validation("assessment.language_unknown");
        }

        var items = layout.Sections.SelectMany(s => s.Items).ToList();
        var questionIds = items.Where(i => i.Kind == SectionItemKind.Question && i.QuestionId is not null).Select(i => i.QuestionId!.Value).Distinct().ToArray();
        var questions = await db.Questions.AsNoTracking().Where(q => questionIds.Contains(q.Id) && !q.Archived)
            .Select(q => new { q.Id, q.BankId }).ToListAsync(ct);
        if (questions.Count != questionIds.Length)
        {
            return Error.Validation("assessment.question_not_found");
        }

        var groupIds = items.Where(i => i.Kind == SectionItemKind.Group && i.GroupId is not null).Select(i => i.GroupId!.Value).Distinct().ToArray();
        var groups = await db.Groups.AsNoTracking().Where(g => groupIds.Contains(g.Id)).Select(g => new { g.Id, g.BankId }).ToListAsync(ct);
        if (groups.Count != groupIds.Length)
        {
            return Error.Validation("assessment.group_not_found");
        }

        var bankIds = questions.Select(q => q.BankId).Concat(groups.Select(g => g.BankId))
            .Concat(items.Where(i => i.Kind == SectionItemKind.Random && i.BankId is not null).Select(i => i.BankId!.Value)).Distinct().ToArray();
        var banks = await db.Banks.AsNoTracking().Where(b => bankIds.Contains(b.Id)).ToListAsync(ct);
        foreach (var bankId in bankIds)
        {
            if (banks.FirstOrDefault(b => b.Id == bankId) is not { } bank || !await access.CanReadBankAsync(bank, actor, ct))
            {
                return Error.Validation("assessment.bank_not_found");
            }
        }

        foreach (var item in items.Where(i => i.Kind == SectionItemKind.Random && i.BankId is not null))
        {
            var available = await QuestionPool.For(db.Questions.AsNoTracking(), item).CountAsync(ct);
            if (available < item.Count)
            {
                return Error.Validation("assessment.random_pool_too_small", new Dictionary<string, object?> { ["available"] = available, ["count"] = item.Count });
            }
        }

        return null;
    }

    private static PaperLayout Clean(PaperLayout? layout)
    {
        layout ??= new PaperLayout();
        var index = 0;
        foreach (var section in layout.Sections)
        {
            index++;
            section.Id = string.IsNullOrWhiteSpace(section.Id) ? $"s{index}" : section.Id.Trim();
            section.Title = section.Title?.Trim() ?? string.Empty;
            section.SkillCode = string.IsNullOrWhiteSpace(section.SkillCode) ? null : section.SkillCode;
            foreach (var item in section.Items)
            {
                item.Tags = item.Tags.Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length > 0).Distinct().ToList();
                item.SkillCode = string.IsNullOrWhiteSpace(item.SkillCode) ? null : item.SkillCode;
                item.LevelCode = string.IsNullOrWhiteSpace(item.LevelCode) ? null : item.LevelCode;
                item.RubricId = string.IsNullOrWhiteSpace(item.RubricId) ? null : item.RubricId;
                if (item.Kind != SectionItemKind.Random)
                {
                    item.Count = 1;
                }
            }
        }

        return layout;
    }

    private async Task<PaperView> ToViewAsync(Paper p, bool canEdit, CancellationToken ct)
    {
        var groupIds = p.Layout.Sections.SelectMany(s => s.Items).Where(i => i.Kind == SectionItemKind.Group && i.GroupId is not null)
            .Select(i => i.GroupId!.Value).ToArray();
        var grouped = groupIds.Length == 0 ? 0 : await db.Questions.CountAsync(q => q.GroupId != null && groupIds.Contains(q.GroupId.Value) && !q.Archived, ct);
        var count = p.Layout.Sections.SelectMany(s => s.Items).Sum(i => i.Kind switch
        {
            SectionItemKind.Question => 1,
            SectionItemKind.Random => i.Count,
            _ => 0,
        }) + grouped;
        int? minutes = p.Layout.Timed ? p.Layout.Sections.Sum(s => s.TimeLimitMinutes ?? 0) : null;
        return new PaperView(p.Id, p.Title, p.LanguageCode, p.FrameworkCode, p.Description, p.ShowRuby, p.Layout, p.OwnerId, canEdit, count, minutes,
            p.Archived, p.UpdatedAt);
    }
}
