using Konnyaku.Academic.Contracts;
using Konnyaku.Academic.Domain;
using Konnyaku.Catalog.Contracts;
using Konnyaku.Crm.Contracts;
using Konnyaku.Organization.Contracts;
using Konnyaku.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Konnyaku.Academic.Application;

public interface IAcademicDbContext
{
    DbSet<StudyProgram> Programs { get; }
    DbSet<CourseLevel> CourseLevels { get; }
    DbSet<CurriculumLesson> Lessons { get; }
    DbSet<Class> Classes { get; }
    DbSet<Enrollment> Enrollments { get; }
    DatabaseFacade Database { get; }

    void Publish(IIntegrationEvent integrationEvent, string? correlationId = null);

    Task<bool> TryAcceptAsync(Guid eventId, string consumer, CancellationToken cancellationToken);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

public sealed record CreateProgramCommand(
    string Code, IReadOnlyDictionary<string, string> Name, string LanguageCode, string FrameworkCode, string? AgeGroup,
    string? Goal, bool IsPublic);

public sealed record ProgramView(
    Guid Id, string Code, IReadOnlyDictionary<string, string> Name, string LanguageCode, string FrameworkCode,
    string? AgeGroup, string? Goal, bool IsPublic, IReadOnlyList<CourseLevelView> Levels);

public sealed record CourseLevelView(
    Guid Id, string Code, IReadOnlyDictionary<string, string> Name, string TargetLevelCode, int Ordinal, int SessionCount, int SessionMinutes);

public sealed class CreateProgramHandler(IAcademicDbContext db, ICatalogApi catalog)
{
    public async Task<Result<ProgramView>> HandleAsync(CreateProgramCommand command, CancellationToken cancellationToken)
    {
        var languages = await catalog.ListLanguagesAsync(cancellationToken);
        if (languages.All(l => l.Code != command.LanguageCode))
        {
            return Error.Validation("academic.language_unknown");
        }

        // A framework is usable when it belongs to the language or is shared (CEFR).
        var framework = await catalog.GetFrameworkAsync(command.FrameworkCode ?? string.Empty, cancellationToken);
        if (framework is null || (framework.LanguageCode is not null && framework.LanguageCode != command.LanguageCode))
        {
            return Error.Validation("academic.framework_not_for_language",
                new Dictionary<string, object?> { ["framework"] = command.FrameworkCode, ["language"] = command.LanguageCode });
        }

        var created = StudyProgram.Create(command.Code, command.Name, command.LanguageCode, framework.Code,
            command.AgeGroup, command.Goal, command.IsPublic);
        if (!created.IsSuccess)
        {
            return created.Error!;
        }

        var program = created.Value;
        if (await db.Programs.AnyAsync(p => p.Code == program.Code, cancellationToken))
        {
            return Error.Conflict("academic.code_taken", new Dictionary<string, object?> { ["code"] = program.Code });
        }

        db.Programs.Add(program);
        await db.SaveChangesAsync(cancellationToken);
        return program.ToView();
    }
}

public sealed record AddCourseLevelCommand(
    string Code, IReadOnlyDictionary<string, string> Name, string TargetLevelCode, int SessionCount, int SessionMinutes);

public sealed class AddCourseLevelHandler(IAcademicDbContext db, ICatalogApi catalog)
{
    public async Task<Result<CourseLevelView>> HandleAsync(Guid programId, AddCourseLevelCommand command, CancellationToken cancellationToken)
    {
        var program = await db.Programs.Include(p => p.Levels).FirstOrDefaultAsync(p => p.Id == programId, cancellationToken);
        if (program is null)
        {
            return Error.NotFound("academic.program_not_found");
        }

        var framework = await catalog.GetFrameworkAsync(program.FrameworkCode, cancellationToken);
        if (framework is null || framework.Levels.All(l => l.Code != command.TargetLevelCode))
        {
            return Error.Validation("academic.level_not_in_framework",
                new Dictionary<string, object?> { ["level"] = command.TargetLevelCode, ["framework"] = program.FrameworkCode });
        }

        var created = CourseLevel.Create(program.Id, command.Code, command.Name, command.TargetLevelCode,
            program.Levels.Count + 1, command.SessionCount, command.SessionMinutes);
        if (!created.IsSuccess)
        {
            return created.Error!;
        }

        if (program.Levels.Any(l => l.Code == created.Value.Code))
        {
            return Error.Conflict("academic.code_taken");
        }

        db.CourseLevels.Add(created.Value);
        await db.SaveChangesAsync(cancellationToken);
        return created.Value.ToView();
    }
}

public sealed record CreateClassCommand(string Code, Guid CourseLevelId, Guid BranchId, DeliveryMode DeliveryMode, int Capacity, DateOnly StartsOn);

public sealed class CreateClassHandler(IAcademicDbContext db, IOrganizationApi organization, IAcademicApi academic)
{
    public async Task<Result<ClassDto>> HandleAsync(CreateClassCommand command, CancellationToken cancellationToken)
    {
        if (!await db.CourseLevels.AnyAsync(l => l.Id == command.CourseLevelId, cancellationToken))
        {
            return Error.NotFound("academic.course_level_not_found");
        }

        // The class's time zone comes from its branch (branches may be in other countries).
        var branch = await organization.GetBranchAsync(command.BranchId, cancellationToken);
        if (branch is null)
        {
            return Error.Validation("academic.branch_unknown");
        }

        var created = Class.Create(command.Code, command.CourseLevelId, branch.Id, command.DeliveryMode, command.Capacity,
            command.StartsOn, branch.TimeZone);
        if (!created.IsSuccess)
        {
            return created.Error!;
        }

        if (await db.Classes.AnyAsync(c => c.Code == created.Value.Code, cancellationToken))
        {
            return Error.Conflict("academic.code_taken");
        }

        db.Classes.Add(created.Value);
        await db.SaveChangesAsync(cancellationToken);
        return (await academic.GetClassAsync(created.Value.Id, cancellationToken))!;
    }
}

public sealed record EnrollCommand(Guid StudentId, EnrollmentType Type, Guid? OrderId = null);

public sealed class EnrollHandler(IAcademicDbContext db, ICrmApi crm, TimeProvider clock)
{
    public async Task<Result<RosterEntryDto>> HandleAsync(Guid classId, EnrollCommand command, CancellationToken cancellationToken)
    {
        var cls = await db.Classes.AsNoTracking().FirstOrDefaultAsync(c => c.Id == classId, cancellationToken);
        if (cls is null)
        {
            return Error.NotFound("academic.class_not_found");
        }

        if (cls.Status is not (ClassStatus.Open or ClassStatus.Running))
        {
            return Error.Conflict("academic.class_not_open");
        }

        if (await crm.GetStudentAsync(command.StudentId, cancellationToken) is null)
        {
            return Error.Validation("academic.student_unknown");
        }

        if (await db.Enrollments.AnyAsync(e => e.ClassId == classId && e.StudentId == command.StudentId &&
                (e.Status == EnrollmentStatus.Active || e.Status == EnrollmentStatus.Frozen), cancellationToken))
        {
            return Error.Conflict("academic.already_enrolled");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Atomic seat reservation: two concurrent enrollments can never exceed capacity.
        var reserved = await db.Classes
            .Where(c => c.Id == classId && c.EnrolledCount < c.Capacity)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.EnrolledCount, c => c.EnrolledCount + 1), cancellationToken);
        if (reserved == 0)
        {
            return Error.Conflict("academic.class_full", new Dictionary<string, object?> { ["capacity"] = cls.Capacity });
        }

        var enrollment = new Enrollment(classId, command.StudentId, command.Type, cls.StartsOn, clock.GetUtcNow(), command.OrderId);
        db.Enrollments.Add(enrollment);
        db.Publish(new EnrollmentActivatedV1(enrollment.Id, classId, command.StudentId, command.Type.ToString(), command.OrderId));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new RosterEntryDto(enrollment.Id, enrollment.StudentId, enrollment.Status.ToString());
    }
}

public sealed record FreezeCommand(DateOnly From, DateOnly To);

public sealed class EnrollmentLifecycleHandler(IAcademicDbContext db)
{
    public Task<Result<RosterEntryDto>> FreezeAsync(Guid enrollmentId, FreezeCommand command, CancellationToken ct) =>
        ChangeAsync(enrollmentId, e => e.Freeze(command.From, command.To), ct);

    public Task<Result<RosterEntryDto>> UnfreezeAsync(Guid enrollmentId, CancellationToken ct) =>
        ChangeAsync(enrollmentId, e => e.Unfreeze(), ct);

    public async Task<Result<RosterEntryDto>> DropAsync(Guid enrollmentId, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var result = await ChangeAsync(enrollmentId, e => e.Drop(), ct, release: true);
        if (result.IsSuccess)
        {
            await transaction.CommitAsync(ct);
        }

        return result;
    }

    private async Task<Result<RosterEntryDto>> ChangeAsync(
        Guid enrollmentId, Func<Enrollment, Result<Enrollment>> change, CancellationToken ct, bool release = false)
    {
        var enrollment = await db.Enrollments.FirstOrDefaultAsync(e => e.Id == enrollmentId, ct);
        if (enrollment is null)
        {
            return Error.NotFound("academic.enrollment_not_found");
        }

        var result = change(enrollment);
        if (!result.IsSuccess)
        {
            return result.Error!;
        }

        if (release)
        {
            await db.Classes.Where(c => c.Id == enrollment.ClassId && c.EnrolledCount > 0)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.EnrolledCount, c => c.EnrolledCount - 1), ct);
            db.Publish(new EnrollmentDroppedV1(enrollment.Id, enrollment.ClassId, enrollment.StudentId));
        }

        await db.SaveChangesAsync(ct);
        return new RosterEntryDto(enrollment.Id, enrollment.StudentId, enrollment.Status.ToString());
    }
}

public sealed class AcademicQueries(IAcademicDbContext db) : IAcademicApi
{
    public async Task<IReadOnlyList<ProgramView>> ListProgramsAsync(bool publicOnly, string? languageCode, CancellationToken ct)
    {
        var query = db.Programs.AsNoTracking().Include(p => p.Levels).ThenInclude(l => l.Lessons).AsQueryable();
        if (publicOnly)
        {
            query = query.Where(p => p.IsPublic);
        }

        if (!string.IsNullOrWhiteSpace(languageCode))
        {
            query = query.Where(p => p.LanguageCode == languageCode);
        }

        return (await query.OrderBy(p => p.Code).ToListAsync(ct)).Select(p => p.ToView()).ToList();
    }

    public async Task<IReadOnlyList<ClassDto>> ListClassesAsync(CancellationToken ct)
    {
        var ids = await db.Classes.AsNoTracking().OrderBy(c => c.StartsOn).Select(c => c.Id).Take(500).ToListAsync(ct);
        var result = new List<ClassDto>();
        foreach (var id in ids)
        {
            result.Add((await GetClassAsync(id, ct))!);
        }

        return result;
    }

    public async Task<ClassDto?> GetClassAsync(Guid classId, CancellationToken cancellationToken) =>
        await (from c in db.Classes.AsNoTracking()
               join l in db.CourseLevels on c.CourseLevelId equals l.Id
               join p in db.Programs on l.ProgramId equals p.Id
               where c.Id == classId
               select new ClassDto(c.Id, c.Code, c.CourseLevelId, c.BranchId, c.DeliveryMode.ToString(), c.Capacity,
                   c.EnrolledCount, c.StartsOn, c.TimeZone, c.Status.ToString(), l.SessionMinutes, p.LanguageCode))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<LessonDto>> GetLessonsAsync(Guid classId, CancellationToken cancellationToken)
    {
        var lessons = await (from c in db.Classes
                             join lesson in db.Lessons on c.CourseLevelId equals lesson.CourseLevelId
                             where c.Id == classId
                             orderby lesson.Ordinal
                             select lesson).AsNoTracking().ToListAsync(cancellationToken);
        return lessons.Select(l => new LessonDto(l.Id, l.Ordinal, l.Title.Values)).ToList();
    }

    public async Task<IReadOnlyList<Guid>> ListClassIdsOfStudentsAsync(IReadOnlyCollection<Guid> studentIds, CancellationToken cancellationToken)
    {
        var ids = studentIds.ToArray();
        return await db.Enrollments.AsNoTracking()
            .Where(e => ids.Contains(e.StudentId) && (e.Status == EnrollmentStatus.Active || e.Status == EnrollmentStatus.Frozen))
            .Select(e => e.ClassId).Distinct().ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RosterEntryDto>> GetRosterAsync(Guid classId, CancellationToken cancellationToken) =>
        await db.Enrollments.AsNoTracking()
            .Where(e => e.ClassId == classId && (e.Status == EnrollmentStatus.Active || e.Status == EnrollmentStatus.Frozen))
            .OrderBy(e => e.CreatedAt)
            .Select(e => new RosterEntryDto(e.Id, e.StudentId, e.Status.ToString()))
            .ToListAsync(cancellationToken);
}

public static class AcademicMapping
{
    public static ProgramView ToView(this StudyProgram p) => new(
        p.Id, p.Code, p.Name.Values, p.LanguageCode, p.FrameworkCode, p.AgeGroup, p.Goal, p.IsPublic,
        p.Levels.OrderBy(l => l.Ordinal).Select(l => l.ToView()).ToList());

    public static CourseLevelView ToView(this CourseLevel l) =>
        new(l.Id, l.Code, l.Name.Values, l.TargetLevelCode, l.Ordinal, l.Lessons.Count, l.SessionMinutes);
}
