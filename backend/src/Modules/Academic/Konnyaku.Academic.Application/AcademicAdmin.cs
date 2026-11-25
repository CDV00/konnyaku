using Konnyaku.Academic.Contracts;
using Konnyaku.Academic.Domain;
using Konnyaku.Application.Authorization;
using Konnyaku.Application.Persistence;
using Konnyaku.Catalog.Contracts;
using Konnyaku.Organization.Contracts;
using Konnyaku.Scheduling.Contracts;
using Konnyaku.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Konnyaku.Academic.Application;

public sealed record UpdateProgramCommand(
    string Code, IReadOnlyDictionary<string, string> Name, string LanguageCode, string FrameworkCode, string? AgeGroup,
    string? Goal, bool IsPublic);

public sealed record UpdateCourseLevelCommand(
    string Code, IReadOnlyDictionary<string, string> Name, string TargetLevelCode, int SessionCount, int SessionMinutes);

/// <summary>Programs and their levels: center-wide catalogue, changed by the tenant admin only.</summary>
public sealed class ProgramAdminHandler(IAcademicDbContext db, ICatalogApi catalog)
{
    public async Task<Result<ProgramView>> UpdateAsync(Guid programId, UpdateProgramCommand command, CancellationToken ct)
    {
        var program = await db.Programs.Include(p => p.Levels).ThenInclude(l => l.Lessons).FirstOrDefaultAsync(p => p.Id == programId, ct);
        if (program is null)
        {
            return Error.NotFound("academic.program_not_found");
        }

        var languages = await catalog.ListLanguagesAsync(ct);
        if (languages.All(l => l.Code != command.LanguageCode))
        {
            return Error.Validation("academic.language_unknown");
        }

        var framework = await catalog.GetFrameworkAsync(command.FrameworkCode ?? string.Empty, ct);
        if (framework is null || (framework.LanguageCode is not null && framework.LanguageCode != command.LanguageCode))
        {
            return Error.Validation("academic.framework_not_for_language",
                new Dictionary<string, object?> { ["framework"] = command.FrameworkCode, ["language"] = command.LanguageCode });
        }

        var updated = program.Update(command.Code, command.Name, command.LanguageCode, framework.Code, command.AgeGroup, command.Goal, command.IsPublic);
        if (!updated.IsSuccess)
        {
            return updated.Error!;
        }

        if (await db.Programs.AnyAsync(p => p.Id != programId && p.Code == program.Code, ct))
        {
            return Error.Conflict("academic.code_taken", new Dictionary<string, object?> { ["code"] = program.Code });
        }

        await db.SaveChangesAsync(ct);
        return program.ToView();
    }

    public async Task<Result<ProgramView>> SetActiveAsync(Guid programId, bool active, CancellationToken ct)
    {
        var program = await db.Programs.Include(p => p.Levels).ThenInclude(l => l.Lessons).FirstOrDefaultAsync(p => p.Id == programId, ct);
        if (program is null)
        {
            return Error.NotFound("academic.program_not_found");
        }

        program.SetActive(active);
        await db.SaveChangesAsync(ct);
        return program.ToView();
    }

    public async Task<Result<CourseLevelView>> UpdateLevelAsync(Guid levelId, UpdateCourseLevelCommand command, CancellationToken ct)
    {
        var level = await db.CourseLevels.Include(l => l.Lessons).FirstOrDefaultAsync(l => l.Id == levelId, ct);
        if (level is null)
        {
            return Error.NotFound("academic.course_level_not_found");
        }

        var program = await db.Programs.AsNoTracking().FirstAsync(p => p.Id == level.ProgramId, ct);
        var framework = await catalog.GetFrameworkAsync(program.FrameworkCode, ct);
        if (framework is null || framework.Levels.All(l => l.Code != command.TargetLevelCode))
        {
            return Error.Validation("academic.level_not_in_framework",
                new Dictionary<string, object?> { ["level"] = command.TargetLevelCode, ["framework"] = program.FrameworkCode });
        }

        var used = await db.Classes.AnyAsync(c => c.CourseLevelId == levelId, ct);
        var existing = level.Lessons.ToHashSet();
        var updated = level.Update(command.Code, command.Name, command.TargetLevelCode, command.SessionCount, command.SessionMinutes, used);
        if (!updated.IsSuccess)
        {
            return updated.Error!;
        }

        // New lessons carry their own id: add them explicitly, or EF would take them for existing rows.
        db.Lessons.AddRange(level.Lessons.Where(l => !existing.Contains(l)));

        if (await db.CourseLevels.AnyAsync(l => l.Id != levelId && l.ProgramId == level.ProgramId && l.Code == level.Code, ct))
        {
            return Error.Conflict("academic.code_taken");
        }

        await db.SaveChangesAsync(ct);
        return level.ToView();
    }

    public async Task<Result<CourseLevelView>> SetLevelActiveAsync(Guid levelId, bool active, CancellationToken ct)
    {
        var level = await db.CourseLevels.Include(l => l.Lessons).FirstOrDefaultAsync(l => l.Id == levelId, ct);
        if (level is null)
        {
            return Error.NotFound("academic.course_level_not_found");
        }

        level.SetActive(active);
        await db.SaveChangesAsync(ct);
        return level.ToView();
    }
}

/// <summary>
/// Who sees which classes, and the branch rule for hand-made enrollments. Members with <see cref="Permissions.ClassesView"/>
/// see every class; a teacher sees the classes they teach. Enrolling, freezing and dropping need
/// <see cref="Permissions.Classes"/> for the class's branch (paid orders enroll through the Worker, without this check).
/// </summary>
/// <summary>
/// Filters of the class list (<c>GET /api/academic/classes</c>): <see cref="Q"/> searches the class code; <see cref="Status"/>
/// is a status name, <c>current</c> (planned, open or running) or empty for all.
/// </summary>
public sealed record ClassFilter(
    string? Q = null, string? Status = null, Guid? Branch = null, string? Language = null, string? Mode = null, Guid? Level = null,
    DateOnly? StartsFrom = null, DateOnly? StartsTo = null, string? Ids = null);

public sealed class ClassAccess(IAcademicDbContext db, ISchedulingApi scheduling, IAccessControl access)
{
    public static readonly IReadOnlySet<string> Sorts = new HashSet<string> { "startsOn", "code", "status", "enrolled" };

    /// <summary>
    /// Classes the member may see (all of them with <see cref="Permissions.ClassesView"/>, otherwise those they teach),
    /// filtered, sorted and paged in the database.
    /// </summary>
    public async Task<Page<ClassDto>> VisibleClassesAsync(Guid userId, ClassFilter filter, PageRequest page, CancellationToken ct)
    {
        var classes = db.Classes.AsNoTracking();
        if (!await access.HasAsync(Permissions.ClassesView, ct))
        {
            var mine = (await scheduling.ListClassIdsOfTeacherAsync(userId, ct)).ToArray();
            classes = classes.Where(c => mine.Contains(c.Id));
        }

        if (TextSearch.Term(filter.Q) is { } term)
        {
            var pattern = TextSearch.Contains(term);
            classes = classes.Where(c => EF.Functions.Like(c.Code.ToLower(), pattern));
        }

        classes = filter.Status switch
        {
            null or "" or "all" => classes,
            "current" => classes.Where(c => c.Status == ClassStatus.Planned || c.Status == ClassStatus.Open || c.Status == ClassStatus.Running),
            var s when Enum.TryParse<ClassStatus>(s, true, out var status) => classes.Where(c => c.Status == status),
            _ => classes.Where(_ => false),
        };
        if (filter.Branch is { } branch)
        {
            classes = classes.Where(c => c.BranchId == branch);
        }

        if (Enum.TryParse<DeliveryMode>(filter.Mode, true, out var mode))
        {
            classes = classes.Where(c => c.DeliveryMode == mode);
        }

        if (filter.Level is { } level)
        {
            classes = classes.Where(c => c.CourseLevelId == level);
        }

        if (filter.StartsFrom is { } from)
        {
            classes = classes.Where(c => c.StartsOn >= from);
        }

        if (filter.StartsTo is { } to)
        {
            classes = classes.Where(c => c.StartsOn <= to);
        }

        // Name lookups of other screens (the classes of a page of orders).
        if (!string.IsNullOrWhiteSpace(filter.Ids))
        {
            var ids = filter.Ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(id => Guid.TryParse(id, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).Distinct().Take(PageRequest.MaxPageSize).ToArray();
            classes = classes.Where(c => ids.Contains(c.Id));
        }

        var rows = from c in classes
                   join l in db.CourseLevels on c.CourseLevelId equals l.Id
                   join p in db.Programs on l.ProgramId equals p.Id
                   select new { Class = c, l.SessionMinutes, p.LanguageCode };
        if (!string.IsNullOrWhiteSpace(filter.Language))
        {
            rows = rows.Where(r => r.LanguageCode == filter.Language);
        }

        var ordered = (page.Sort, page.Descending) switch
        {
            ("code", var desc) => desc ? rows.OrderByDescending(r => r.Class.Code).ThenByDescending(r => r.Class.Id) : rows.OrderBy(r => r.Class.Code).ThenBy(r => r.Class.Id),
            ("status", var desc) => desc ? rows.OrderByDescending(r => r.Class.Status).ThenBy(r => r.Class.Code).ThenBy(r => r.Class.Id) : rows.OrderBy(r => r.Class.Status).ThenBy(r => r.Class.Code).ThenBy(r => r.Class.Id),
            ("enrolled", var desc) => desc ? rows.OrderByDescending(r => r.Class.EnrolledCount).ThenBy(r => r.Class.Code).ThenBy(r => r.Class.Id) : rows.OrderBy(r => r.Class.EnrolledCount).ThenBy(r => r.Class.Code).ThenBy(r => r.Class.Id),
            (_, true) => rows.OrderByDescending(r => r.Class.StartsOn).ThenByDescending(r => r.Class.Code).ThenByDescending(r => r.Class.Id),
            _ => rows.OrderBy(r => r.Class.StartsOn).ThenBy(r => r.Class.Code).ThenBy(r => r.Class.Id),
        };
        return await ordered
            .Select(r => new ClassDto(r.Class.Id, r.Class.Code, r.Class.CourseLevelId, r.Class.BranchId, r.Class.DeliveryMode.ToString(),
                r.Class.Capacity, r.Class.EnrolledCount, r.Class.StartsOn, r.Class.TimeZone, r.Class.Status.ToString(), r.SessionMinutes, r.LanguageCode))
            .ToPageAsync(page, ct);
    }

    public async Task<bool> CanSeeAsync(Guid classId, Guid userId, CancellationToken ct) =>
        await access.HasAsync(Permissions.ClassesView, ct) || (await scheduling.ListClassIdsOfTeacherAsync(userId, ct)).Contains(classId);

    /// <summary>Null when allowed or when the class does not exist (the handler then answers not found).</summary>
    public async Task<Error?> EnsureEnrollmentBranchAsync(Guid? classId, Guid? enrollmentId, CancellationToken ct)
    {
        var id = classId ?? await db.Enrollments.Where(e => e.Id == enrollmentId).Select(e => (Guid?)e.ClassId).FirstOrDefaultAsync(ct);
        var branchId = id is null ? null : await db.Classes.Where(c => c.Id == id).Select(c => (Guid?)c.BranchId).FirstOrDefaultAsync(ct);
        return branchId is null ? null : await access.EnsureAsync(Permissions.Classes, ct, branchId);
    }
}

public sealed record UpdateClassCommand(string Code, Guid CourseLevelId, Guid BranchId, DeliveryMode DeliveryMode, int Capacity, DateOnly StartsOn);

public sealed record CloseClassCommand(ClassStatus Status);

/// <summary>
/// Edits and closes classes. Branch managers and coordinators act on classes of their branches only (moving a class
/// needs both branches). Delivery-mode changes and closing reach the schedule through events.
/// </summary>
public sealed class ClassAdminHandler(
    IAcademicDbContext db, IOrganizationApi organization, ISchedulingApi scheduling, IAcademicApi academic, IAccessControl access)
{
    public async Task<Result<ClassDto>> UpdateAsync(Guid classId, UpdateClassCommand command, CancellationToken ct)
    {
        var cls = await db.Classes.FirstOrDefaultAsync(c => c.Id == classId, ct);
        if (cls is null)
        {
            return Error.NotFound("academic.class_not_found");
        }

        if (await access.EnsureAsync(Permissions.Classes, ct, cls.BranchId, command.BranchId) is { } forbidden)
        {
            return forbidden;
        }

        var timeZone = cls.TimeZone;
        if (command.BranchId != cls.BranchId)
        {
            var branch = await organization.GetBranchAsync(command.BranchId, ct);
            if (branch is null)
            {
                return Error.Validation("academic.branch_unknown");
            }

            if (!branch.IsActive)
            {
                return Error.Conflict("academic.branch_inactive");
            }

            timeZone = branch.TimeZone;
        }

        if (command.CourseLevelId != cls.CourseLevelId &&
            !await db.CourseLevels.AnyAsync(l => l.Id == command.CourseLevelId && l.IsActive, ct))
        {
            return Error.Validation("academic.course_level_unavailable");
        }

        var hasSessions = await scheduling.CountClassSessionsAsync(classId, ct) > 0;
        var previousMode = cls.DeliveryMode;
        var updated = cls.Update(new ClassChange(command.Code, command.CourseLevelId, command.BranchId, timeZone, command.DeliveryMode,
            command.Capacity, command.StartsOn), hasSessions);
        if (!updated.IsSuccess)
        {
            return updated.Error!;
        }

        if (await db.Classes.AnyAsync(c => c.Id != classId && c.Code == cls.Code, ct))
        {
            return Error.Conflict("academic.code_taken");
        }

        if (previousMode != cls.DeliveryMode)
        {
            db.Publish(new ClassDeliveryModeChangedV1(cls.Id, cls.DeliveryMode.ToString()));
        }

        await db.SaveChangesAsync(ct);
        return (await academic.GetClassAsync(classId, ct))!;
    }

    public Task<Result<ClassDto>> CloseAsync(Guid classId, CloseClassCommand command, CancellationToken ct) =>
        ChangeStatusAsync(classId, cls => cls.Close(command.Status), publishClosed: true, ct);

    public Task<Result<ClassDto>> ReopenAsync(Guid classId, CancellationToken ct) =>
        ChangeStatusAsync(classId, cls => cls.Reopen(), publishClosed: false, ct);

    private async Task<Result<ClassDto>> ChangeStatusAsync(Guid classId, Func<Class, Result<Class>> change, bool publishClosed, CancellationToken ct)
    {
        var cls = await db.Classes.FirstOrDefaultAsync(c => c.Id == classId, ct);
        if (cls is null)
        {
            return Error.NotFound("academic.class_not_found");
        }

        if (await access.EnsureAsync(Permissions.Classes, ct, cls.BranchId) is { } forbidden)
        {
            return forbidden;
        }

        var changed = change(cls);
        if (!changed.IsSuccess)
        {
            return changed.Error!;
        }

        if (publishClosed)
        {
            db.Publish(new ClassClosedV1(cls.Id, cls.Status.ToString()));
        }

        await db.SaveChangesAsync(ct);
        return (await academic.GetClassAsync(classId, ct))!;
    }
}
