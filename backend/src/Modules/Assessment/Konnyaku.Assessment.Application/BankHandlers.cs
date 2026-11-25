using Konnyaku.Assessment.Domain;
using Konnyaku.Catalog.Contracts;
using Konnyaku.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Konnyaku.Assessment.Application;

public sealed record BankCommand(string Name, string LanguageCode, Guid? ProgramId, BankVisibility Visibility, string? Description);

public sealed record BankView(
    Guid Id, string Name, string LanguageCode, Guid? ProgramId, BankVisibility Visibility, string? Description, Guid OwnerId, int QuestionCount,
    bool CanEdit, DateTimeOffset UpdatedAt);

public sealed record GroupCommand(string Title, string Passage);

public sealed record GroupView(Guid Id, Guid BankId, string Title, string Passage, int QuestionCount);

public sealed record QuestionCommand(
    QuestionType Type, string Stem, QuestionContent? Content, AnswerKey? Key, decimal Points, string? FrameworkCode, string? SkillCode,
    string? LevelCode, IReadOnlyList<string>? Tags, string? Explanation, Guid? GroupId);

public sealed record QuestionView(
    Guid Id, Guid BankId, Guid? GroupId, QuestionType Type, string Stem, QuestionContent Content, AnswerKey Key, decimal Points,
    string? FrameworkCode, string? SkillCode, string? LevelCode, IReadOnlyList<string> Tags, string? Explanation, int Version, bool Archived,
    DateTimeOffset UpdatedAt);

public sealed record BankDetail(BankView Bank, IReadOnlyList<GroupView> Groups, IReadOnlyList<QuestionView> Questions);

/// <summary>Question banks, reading groups and questions.</summary>
public sealed class BankHandlers(IAssessmentDbContext db, AssessmentAccess access, ICatalogApi catalog, IRichText richText, TimeProvider clock)
{
    public const int MaxQuestionsListed = 2000;

    public async Task<IReadOnlyList<BankView>> ListAsync(Actor actor, string? language, CancellationToken ct)
    {
        if (!await access.CanAuthorAsync(actor, ct))
        {
            return [];
        }

        var manager = await access.IsManagerAsync(ct);
        var banks = db.Banks.AsNoTracking();
        if (!manager)
        {
            banks = banks.Where(b => b.OwnerId == actor.UserId || b.Visibility == BankVisibility.Shared);
        }

        if (!string.IsNullOrWhiteSpace(language))
        {
            banks = banks.Where(b => b.LanguageCode == language);
        }

        var rows = await banks.OrderBy(b => b.LanguageCode).ThenBy(b => b.Name)
            .Select(b => new { Bank = b, Count = db.Questions.Count(q => q.BankId == b.Id && !q.Archived) })
            .ToListAsync(ct);
        return rows.Select(r => ToView(r.Bank, r.Count, manager || r.Bank.OwnerId == actor.UserId)).ToList();
    }

    public async Task<Result<BankView>> CreateAsync(BankCommand command, Actor actor, CancellationToken ct)
    {
        if (!await access.CanAuthorAsync(actor, ct))
        {
            return AssessmentAccess.Forbidden;
        }

        if (await ValidateBankAsync(command, ct) is { } error)
        {
            return error;
        }

        var bank = new QuestionBank(command.Name, command.LanguageCode, command.ProgramId, command.Visibility, command.Description, actor.UserId,
            clock.GetUtcNow());
        db.Banks.Add(bank);
        await db.SaveChangesAsync(ct);
        return ToView(bank, 0, true);
    }

    public async Task<Result<BankView>> UpdateAsync(Guid id, BankCommand command, Actor actor, CancellationToken ct)
    {
        var (bank, error) = await EditableBankAsync(id, actor, ct);
        if (error is not null)
        {
            return error;
        }

        if (await ValidateBankAsync(command, ct) is { } invalid)
        {
            return invalid;
        }

        bank!.Update(command.Name, command.LanguageCode, command.ProgramId, command.Visibility, command.Description, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return ToView(bank, await db.Questions.CountAsync(q => q.BankId == id && !q.Archived, ct), true);
    }

    public async Task<Result<BankDetail>> GetAsync(Guid id, Actor actor, bool includeArchived, CancellationToken ct)
    {
        var bank = await db.Banks.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);
        if (bank is null || !await access.CanReadBankAsync(bank, actor, ct))
        {
            return Error.NotFound("assessment.bank_not_found");
        }

        var questions = await db.Questions.AsNoTracking().Where(q => q.BankId == id && (includeArchived || !q.Archived))
            .OrderBy(q => q.GroupId).ThenBy(q => q.Id).Take(MaxQuestionsListed).ToListAsync(ct);
        var groups = await db.Groups.AsNoTracking().Where(g => g.BankId == id).OrderBy(g => g.Title).ToListAsync(ct);
        var canEdit = await access.CanEditAsync(bank.OwnerId, actor, ct);
        return new BankDetail(
            ToView(bank, questions.Count(q => !q.Archived), canEdit),
            groups.Select(g => new GroupView(g.Id, g.BankId, g.Title, g.Passage, questions.Count(q => q.GroupId == g.Id && !q.Archived))).ToList(),
            questions.Select(ToView).ToList());
    }

    public async Task<Result<GroupView>> CreateGroupAsync(Guid bankId, GroupCommand command, Actor actor, CancellationToken ct)
    {
        var (_, error) = await EditableBankAsync(bankId, actor, ct);
        if (error is not null)
        {
            return error;
        }

        if (string.IsNullOrWhiteSpace(command.Title) || string.IsNullOrWhiteSpace(QuestionRules.StripTags(richText.Sanitize(command.Passage))))
        {
            return Error.Validation("assessment.passage_required");
        }

        var group = new QuestionGroup(bankId, command.Title, richText.Sanitize(command.Passage), clock.GetUtcNow());
        db.Groups.Add(group);
        await db.SaveChangesAsync(ct);
        return new GroupView(group.Id, bankId, group.Title, group.Passage, 0);
    }

    public async Task<Result<GroupView>> UpdateGroupAsync(Guid groupId, GroupCommand command, Actor actor, CancellationToken ct)
    {
        var group = await db.Groups.FirstOrDefaultAsync(g => g.Id == groupId, ct);
        if (group is null)
        {
            return Error.NotFound("assessment.group_not_found");
        }

        var (_, error) = await EditableBankAsync(group.BankId, actor, ct);
        if (error is not null)
        {
            return error;
        }

        if (string.IsNullOrWhiteSpace(command.Title) || string.IsNullOrWhiteSpace(QuestionRules.StripTags(richText.Sanitize(command.Passage))))
        {
            return Error.Validation("assessment.passage_required");
        }

        group.Update(command.Title, richText.Sanitize(command.Passage), clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return new GroupView(group.Id, group.BankId, group.Title, group.Passage, await db.Questions.CountAsync(q => q.GroupId == groupId && !q.Archived, ct));
    }

    public async Task<Result<QuestionView>> CreateQuestionAsync(Guid bankId, QuestionCommand command, Actor actor, CancellationToken ct)
    {
        var (_, error) = await EditableBankAsync(bankId, actor, ct);
        if (error is not null)
        {
            return error;
        }

        var data = await ToDataAsync(bankId, command, ct);
        if (!data.IsSuccess)
        {
            return data.Error!;
        }

        var created = Question.Create(bankId, data.Value, actor.UserId, clock.GetUtcNow());
        if (!created.IsSuccess)
        {
            return created.Error!;
        }

        db.Questions.Add(created.Value);
        await db.SaveChangesAsync(ct);
        return ToView(created.Value);
    }

    /// <summary>A new version of the question; attempts already given keep the version they were given.</summary>
    public async Task<Result<QuestionView>> UpdateQuestionAsync(Guid id, QuestionCommand command, Actor actor, CancellationToken ct)
    {
        var question = await db.Questions.FirstOrDefaultAsync(q => q.Id == id, ct);
        if (question is null)
        {
            return Error.NotFound("assessment.question_not_found");
        }

        var (_, error) = await EditableBankAsync(question.BankId, actor, ct);
        if (error is not null)
        {
            return error;
        }

        var data = await ToDataAsync(question.BankId, command, ct);
        if (!data.IsSuccess)
        {
            return data.Error!;
        }

        var updated = question.Update(data.Value, clock.GetUtcNow());
        if (!updated.IsSuccess)
        {
            return updated.Error!;
        }

        await db.SaveChangesAsync(ct);
        return ToView(question);
    }

    /// <summary>Archived questions are no longer drawn or added to papers; past attempts keep their snapshot.</summary>
    public async Task<Result<QuestionView>> SetArchivedAsync(Guid id, bool archived, Actor actor, CancellationToken ct)
    {
        var question = await db.Questions.FirstOrDefaultAsync(q => q.Id == id, ct);
        if (question is null)
        {
            return Error.NotFound("assessment.question_not_found");
        }

        var (_, error) = await EditableBankAsync(question.BankId, actor, ct);
        if (error is not null)
        {
            return error;
        }

        if (archived)
        {
            question.Archive(clock.GetUtcNow());
        }
        else
        {
            question.Restore(clock.GetUtcNow());
        }

        await db.SaveChangesAsync(ct);
        return ToView(question);
    }

    private async Task<(QuestionBank? Bank, Error? Error)> EditableBankAsync(Guid id, Actor actor, CancellationToken ct)
    {
        var bank = await db.Banks.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (bank is null || !await access.CanReadBankAsync(bank, actor, ct))
        {
            return (null, Error.NotFound("assessment.bank_not_found"));
        }

        return await access.CanEditAsync(bank.OwnerId, actor, ct) ? (bank, null) : (null, AssessmentAccess.Forbidden);
    }

    private async Task<Error?> ValidateBankAsync(BankCommand command, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command.Name) || command.Name.Length > 200)
        {
            return Error.Validation("assessment.name_required");
        }

        var languages = await catalog.ListLanguagesAsync(ct);
        return languages.Any(l => l.Code == command.LanguageCode) ? null : Error.Validation("assessment.language_unknown");
    }

    private async Task<Result<QuestionData>> ToDataAsync(Guid bankId, QuestionCommand c, CancellationToken ct)
    {
        if (c.GroupId is { } groupId && !await db.Groups.AnyAsync(g => g.Id == groupId && g.BankId == bankId, ct))
        {
            return Error.Validation("assessment.group_not_found");
        }

        if (c.FrameworkCode is { Length: > 0 } frameworkCode && (c.SkillCode is not null || c.LevelCode is not null))
        {
            var framework = await catalog.GetFrameworkAsync(frameworkCode, ct);
            if (framework is null)
            {
                return Error.Validation("assessment.framework_unknown");
            }

            if (c.SkillCode is not null && framework.Skills.All(s => s.Code != c.SkillCode))
            {
                return Error.Validation("assessment.skill_unknown");
            }

            if (c.LevelCode is not null && framework.Levels.All(l => l.Code != c.LevelCode))
            {
                return Error.Validation("assessment.level_unknown");
            }
        }

        var content = c.Content ?? new QuestionContent();
        var cleanContent = new QuestionContent
        {
            Options = content.Options.Select(o => new ChoiceOption { Id = o.Id?.Trim() ?? string.Empty, Html = richText.Sanitize(o.Html) }).ToList(),
            Targets = content.Targets.Select(o => new ChoiceOption { Id = o.Id?.Trim() ?? string.Empty, Html = richText.Sanitize(o.Html) }).ToList(),
            Essay = content.Essay,
            Shuffle = content.Shuffle,
        };
        return new QuestionData(c.Type, richText.Sanitize(c.Stem), cleanContent, c.Key ?? new AnswerKey(), c.Points, c.FrameworkCode,
            NullIfEmpty(c.SkillCode), NullIfEmpty(c.LevelCode), c.Tags ?? [], c.Explanation is null ? null : richText.Sanitize(c.Explanation), c.GroupId);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static BankView ToView(QuestionBank b, int count, bool canEdit) =>
        new(b.Id, b.Name, b.LanguageCode, b.ProgramId, b.Visibility, b.Description, b.OwnerId, count, canEdit, b.UpdatedAt);

    public static QuestionView ToView(Question q) => new(q.Id, q.BankId, q.GroupId, q.Type, q.Stem, q.Content, q.Key, q.Points, q.FrameworkCode,
        q.SkillCode, q.LevelCode, q.Tags, q.Explanation, q.Version, q.Archived, q.UpdatedAt);
}
