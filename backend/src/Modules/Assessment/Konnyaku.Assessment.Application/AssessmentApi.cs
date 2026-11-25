using Konnyaku.Academic.Contracts;
using Konnyaku.Assessment.Contracts;
using Konnyaku.Assessment.Domain;
using Microsoft.EntityFrameworkCore;

namespace Konnyaku.Assessment.Application;

/// <summary>Read side for other modules (notifications of new assignments and due dates).</summary>
public sealed class AssessmentApi(IAssessmentDbContext db, IAcademicApi academic) : IAssessmentApi
{
    public const string ActiveEnrollment = "Active";

    public async Task<AssignmentAudienceDto?> GetAssignmentAudienceAsync(Guid assignmentId, CancellationToken cancellationToken)
    {
        var assignment = await db.Assignments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == assignmentId, cancellationToken);
        if (assignment is null)
        {
            return null;
        }

        IReadOnlyList<Guid> students = assignment.Target == AssignmentTarget.Students
            ? assignment.StudentIds
            : (await academic.GetRosterAsync(assignment.ClassId, cancellationToken))
                .Where(e => e.Status == ActiveEnrollment).Select(e => e.StudentId).Distinct().ToList();
        var submitted = await db.Attempts.AsNoTracking()
            .Where(t => t.AssignmentId == assignmentId && (t.Status == AttemptStatus.Submitted || t.Status == AttemptStatus.Graded))
            .Select(t => t.StudentId).Distinct().ToListAsync(cancellationToken);
        return new AssignmentAudienceDto(assignment.Id, assignment.ClassId, assignment.Title, assignment.Kind.ToString(), assignment.OpensAt,
            assignment.DueAt, students, submitted);
    }
}
