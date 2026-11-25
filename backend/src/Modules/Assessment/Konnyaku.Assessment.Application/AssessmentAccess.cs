using Konnyaku.Academic.Contracts;
using Konnyaku.Application.Authorization;
using Konnyaku.Assessment.Domain;
using Konnyaku.Crm.Contracts;
using Konnyaku.Scheduling.Contracts;
using Konnyaku.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Konnyaku.Assessment.Application;

public interface IAssessmentDbContext
{
    DbSet<QuestionBank> Banks { get; }
    DbSet<QuestionGroup> Groups { get; }
    DbSet<Question> Questions { get; }
    DbSet<Paper> Papers { get; }
    DbSet<Assignment> Assignments { get; }
    DbSet<Attempt> Attempts { get; }
    DbSet<AttemptItem> AttemptItems { get; }
    DbSet<AttemptEvent> AttemptEvents { get; }

    void Publish(IIntegrationEvent integrationEvent, string? correlationId = null);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>Forgets tracked changes after a lost race, so the next save does not repeat them.</summary>
    void Reset();
}

/// <summary>Allow-list HTML for rich text (stems, passages, explanations); keeps <c>&lt;ruby&gt;</c> for furigana and pinyin.</summary>
public interface IRichText
{
    string Sanitize(string? html);
}

/// <summary>The signed-in staff member (teacher role from the token: a role change revokes the token).</summary>
public sealed record Actor(Guid UserId, bool IsTeacher);

/// <summary>
/// Who may do what. Members with <see cref="Permissions.Assessments"/> manage every bank and paper and the assignments of
/// the classes of their branches; a teacher manages their own banks and papers (and reads shared ones) and the
/// assignments of the classes they teach. Other staff have no access.
/// </summary>
public sealed class AssessmentAccess(IAccessControl access, ISchedulingApi scheduling, IAcademicApi academic)
{
    public static readonly Error Forbidden = Error.Forbidden("access.forbidden");

    public Task<bool> IsManagerAsync(CancellationToken ct) => access.HasAsync(Permissions.Assessments, ct);

    public async Task<bool> CanAuthorAsync(Actor actor, CancellationToken ct) => actor.IsTeacher || await IsManagerAsync(ct);

    public async Task<bool> CanEditAsync(Guid ownerId, Actor actor, CancellationToken ct) =>
        (ownerId == actor.UserId && await CanAuthorAsync(actor, ct)) || await IsManagerAsync(ct);

    public async Task<bool> CanReadBankAsync(QuestionBank bank, Actor actor, CancellationToken ct) =>
        (bank.Visibility == BankVisibility.Shared && await CanAuthorAsync(actor, ct)) || await CanEditAsync(bank.OwnerId, actor, ct);

    /// <summary>Teacher of the class, or assessment manager of its branch.</summary>
    public async Task<bool> CanManageClassAsync(Guid classId, Guid branchId, Actor actor, CancellationToken ct) =>
        await access.CanAsync(Permissions.Assessments, branchId, ct) ||
        (actor.IsTeacher && (await scheduling.ListClassIdsOfTeacherAsync(actor.UserId, ct)).Contains(classId));

    /// <summary>Assignments the member may see: classes they teach, plus every class of the branches they manage.</summary>
    public async Task<IQueryable<Assignment>> VisibleAssignmentsAsync(IQueryable<Assignment> query, Actor actor, CancellationToken ct)
    {
        var scope = (await access.ListAsync(ct)).FirstOrDefault(s => s.Permission == Permissions.Assessments);
        if (scope?.TenantWide == true)
        {
            return query;
        }

        var branches = scope?.BranchIds.ToArray() ?? [];
        var classes = actor.IsTeacher ? (await scheduling.ListClassIdsOfTeacherAsync(actor.UserId, ct)).ToArray() : [];
        return query.Where(a => branches.Contains(a.BranchId) || classes.Contains(a.ClassId));
    }

    public Task<ClassDto?> GetClassAsync(Guid classId, CancellationToken ct) => academic.GetClassAsync(classId, ct);
}

/// <summary>The learner's student profiles and the classes they sit in.</summary>
public sealed class LearnerScope(ICrmApi crm, IAcademicApi academic)
{
    public async Task<(IReadOnlyList<Guid> Students, IReadOnlyList<Guid> Classes)> OfUserAsync(Guid userId, CancellationToken ct)
    {
        var students = (await crm.ListStudentsOfUserAsync(userId, ct)).Select(s => s.Id).ToList();
        if (students.Count == 0)
        {
            return ([], []);
        }

        return (students, await academic.ListClassIdsOfStudentsAsync(students, ct));
    }

    /// <summary>The learner's student profile an assignment is for (a class roster member it targets), if any.</summary>
    public async Task<Guid?> StudentForAsync(Assignment assignment, IReadOnlyList<Guid> students, CancellationToken ct)
    {
        var roster = await academic.GetRosterAsync(assignment.ClassId, ct);
        return students.FirstOrDefault(s => roster.Any(r => r.StudentId == s) && assignment.IsFor(s)) is var id && id != Guid.Empty ? id : null;
    }
}
