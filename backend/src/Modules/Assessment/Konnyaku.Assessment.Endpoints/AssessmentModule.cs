using System.Security.Claims;
using Konnyaku.Application.Authorization;
using Konnyaku.Assessment.Application;
using Konnyaku.Assessment.Contracts;
using Konnyaku.Assessment.Infrastructure;
using Konnyaku.Infrastructure.Auth;
using Konnyaku.Infrastructure.Http;
using Konnyaku.Infrastructure.Messaging;
using Konnyaku.Infrastructure.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Konnyaku.Assessment.Endpoints;

/// <summary>
/// One assessment engine for homework, quizzes, tests, mock exams and placement: question banks, papers, assignments,
/// attempts, grading. Staff routes check ownership, class teaching and <see cref="Permissions.Assessments"/> per branch in
/// the handlers; learner routes (<c>/my</c>) only ever reach the signed-in learner's own assignments and attempts.
/// </summary>
public sealed class AssessmentModule : IModule
{
    public string Name => "assessment";

    public IReadOnlyList<Type> DbContextTypes => [typeof(AssessmentDbContext)];

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddAssessmentInfrastructure(configuration, Name);
        services.AddScoped<AssessmentAccess>();
        services.AddScoped<LearnerScope>();
        services.AddScoped<AttemptService>();
        services.AddScoped<ExpiredAttempts>();
        services.AddScoped<BankHandlers>();
        services.AddScoped<PaperHandlers>();
        services.AddScoped<AssignmentHandlers>();
        services.AddScoped<LearnerHandlers>();
        services.AddScoped<IAssessmentApi, AssessmentApi>();
    }

    public void AddWorkerServices(IServiceCollection services)
    {
        services.AddOutboxProcessor<AssessmentDbContext>();
        services.AddAssessmentWorker();
    }

    public void MapEndpoints(RouteGroupBuilder group)
    {
        var staff = group.MapGroup("").RequireAuthorization(Policies.Staff);

        // Question banks.
        staff.MapGet("/banks", async (string? language, ClaimsPrincipal user, BankHandlers h, CancellationToken ct) =>
            Results.Ok(await h.ListAsync(Actor(user), language, ct)));
        staff.MapPost("/banks", async (BankCommand command, ClaimsPrincipal user, BankHandlers h, CancellationToken ct) =>
            ApiResults.From(await h.CreateAsync(command, Actor(user), ct), b => Results.Created($"banks/{b.Id}", b)));
        staff.MapGet("/banks/{id:guid}", async (Guid id, bool? archived, ClaimsPrincipal user, BankHandlers h, CancellationToken ct) =>
            ApiResults.From(await h.GetAsync(id, Actor(user), archived == true, ct), Results.Ok));
        staff.MapPut("/banks/{id:guid}", async (Guid id, BankCommand command, ClaimsPrincipal user, BankHandlers h, CancellationToken ct) =>
            ApiResults.From(await h.UpdateAsync(id, command, Actor(user), ct), Results.Ok));
        staff.MapPost("/banks/{id:guid}/groups", async (Guid id, GroupCommand command, ClaimsPrincipal user, BankHandlers h, CancellationToken ct) =>
            ApiResults.From(await h.CreateGroupAsync(id, command, Actor(user), ct), g => Results.Created($"groups/{g.Id}", g)));
        staff.MapPut("/groups/{id:guid}", async (Guid id, GroupCommand command, ClaimsPrincipal user, BankHandlers h, CancellationToken ct) =>
            ApiResults.From(await h.UpdateGroupAsync(id, command, Actor(user), ct), Results.Ok));
        staff.MapPost("/banks/{id:guid}/questions", async (Guid id, QuestionCommand command, ClaimsPrincipal user, BankHandlers h, CancellationToken ct) =>
            ApiResults.From(await h.CreateQuestionAsync(id, command, Actor(user), ct), q => Results.Created($"questions/{q.Id}", q)));
        staff.MapPut("/questions/{id:guid}", async (Guid id, QuestionCommand command, ClaimsPrincipal user, BankHandlers h, CancellationToken ct) =>
            ApiResults.From(await h.UpdateQuestionAsync(id, command, Actor(user), ct), Results.Ok));
        staff.MapPost("/questions/{id:guid}/archive", async (Guid id, ClaimsPrincipal user, BankHandlers h, CancellationToken ct) =>
            ApiResults.From(await h.SetArchivedAsync(id, true, Actor(user), ct), Results.Ok));
        staff.MapPost("/questions/{id:guid}/restore", async (Guid id, ClaimsPrincipal user, BankHandlers h, CancellationToken ct) =>
            ApiResults.From(await h.SetArchivedAsync(id, false, Actor(user), ct), Results.Ok));

        // Papers.
        staff.MapGet("/papers", async (bool? archived, ClaimsPrincipal user, PaperHandlers h, CancellationToken ct) =>
            Results.Ok(await h.ListAsync(Actor(user), archived == true, ct)));
        staff.MapPost("/papers", async (PaperCommand command, ClaimsPrincipal user, PaperHandlers h, CancellationToken ct) =>
            ApiResults.From(await h.CreateAsync(command, Actor(user), ct), p => Results.Created($"papers/{p.Id}", p)));
        staff.MapGet("/papers/{id:guid}", async (Guid id, ClaimsPrincipal user, PaperHandlers h, CancellationToken ct) =>
            ApiResults.From(await h.GetAsync(id, Actor(user), ct), Results.Ok));
        staff.MapPut("/papers/{id:guid}", async (Guid id, PaperCommand command, ClaimsPrincipal user, PaperHandlers h, CancellationToken ct) =>
            ApiResults.From(await h.UpdateAsync(id, command, Actor(user), ct), Results.Ok));
        staff.MapPost("/papers/{id:guid}/archive", async (Guid id, ClaimsPrincipal user, PaperHandlers h, CancellationToken ct) =>
            ApiResults.From(await h.SetArchivedAsync(id, true, Actor(user), ct), Results.Ok));
        staff.MapPost("/papers/{id:guid}/restore", async (Guid id, ClaimsPrincipal user, PaperHandlers h, CancellationToken ct) =>
            ApiResults.From(await h.SetArchivedAsync(id, false, Actor(user), ct), Results.Ok));

        // Assignments and grading.
        staff.MapGet("/assignments", async ([AsParameters] AssignmentFilter filter, ClaimsPrincipal user, AssignmentHandlers h, CancellationToken ct) =>
            Results.Ok(await h.ListAsync(Actor(user), filter, ct)));
        staff.MapPost("/assignments", async (AssignmentCommand command, ClaimsPrincipal user, AssignmentHandlers h, CancellationToken ct) =>
            ApiResults.From(await h.CreateAsync(command, Actor(user), ct), a => Results.Created($"assignments/{a.Id}", a)));
        staff.MapGet("/assignments/{id:guid}", async (Guid id, ClaimsPrincipal user, AssignmentHandlers h, CancellationToken ct) =>
            ApiResults.From(await h.GetAsync(id, Actor(user), ct), Results.Ok));
        staff.MapPut("/assignments/{id:guid}", async (Guid id, UpdateAssignmentCommand command, ClaimsPrincipal user, AssignmentHandlers h, CancellationToken ct) =>
            ApiResults.From(await h.UpdateAsync(id, command, Actor(user), ct), Results.Ok));
        staff.MapDelete("/assignments/{id:guid}", async (Guid id, ClaimsPrincipal user, AssignmentHandlers h, CancellationToken ct) =>
            ApiResults.From(await h.DeleteAsync(id, Actor(user), ct), _ => Results.NoContent()));
        staff.MapPost("/assignments/{id:guid}/release", async (Guid id, ClaimsPrincipal user, AssignmentHandlers h, CancellationToken ct) =>
            ApiResults.From(await h.ReleaseAsync(id, Actor(user), ct), Results.Ok));
        staff.MapGet("/classes/{id:guid}/students", async (Guid id, ClaimsPrincipal user, AssignmentHandlers h, CancellationToken ct) =>
            ApiResults.From(await h.StudentsAsync(id, Actor(user), ct), Results.Ok));
        staff.MapGet("/grading", async ([AsParameters] GradingFilter filter, ClaimsPrincipal user, AssignmentHandlers h, CancellationToken ct) =>
            Results.Ok(await h.QueueAsync(Actor(user), filter, ct)));
        staff.MapGet("/attempts/{id:guid}", async (Guid id, ClaimsPrincipal user, AssignmentHandlers h, CancellationToken ct) =>
            ApiResults.From(await h.ReviewAsync(id, Actor(user), ct), Results.Ok));
        staff.MapPut("/attempts/{id:guid}/items/{itemId:guid}/grade", async (Guid id, Guid itemId, GradeItemCommand command, ClaimsPrincipal user,
                AssignmentHandlers h, CancellationToken ct) =>
            ApiResults.From(await h.GradeItemAsync(id, itemId, command, Actor(user), ct), Results.Ok));
        staff.MapPost("/attempts/{id:guid}/finalize", async (Guid id, FinalizeCommand command, ClaimsPrincipal user, AssignmentHandlers h, CancellationToken ct) =>
            ApiResults.From(await h.FinalizeAsync(id, command, Actor(user), ct), Results.Ok));

        // Read-only figures of the branches a manager runs (ops).
        group.MapGet("/overview", async (ClaimsPrincipal user, AssignmentHandlers h, CancellationToken ct) =>
                Results.Ok(await h.OverviewAsync(Actor(user), ct)))
            .RequirePermission(Permissions.Assessments);

        // Learner app: own assignments and attempts only.
        var my = group.MapGroup("/my").RequireAuthorization(Policies.Learn);
        my.MapGet("/assignments", async (ClaimsPrincipal user, LearnerHandlers h, CancellationToken ct) =>
            Results.Ok(await h.ListAsync(user.UserId()!.Value, ct)));
        my.MapGet("/assignments/{id:guid}", async (Guid id, ClaimsPrincipal user, LearnerHandlers h, CancellationToken ct) =>
            ApiResults.From(await h.GetAsync(id, user.UserId()!.Value, ct), Results.Ok));
        my.MapPost("/assignments/{id:guid}/attempts", async (Guid id, ClaimsPrincipal user, LearnerHandlers h, CancellationToken ct) =>
            ApiResults.From(await h.StartAsync(id, user.UserId()!.Value, ct), Results.Ok));
        my.MapGet("/attempts/{id:guid}", async (Guid id, ClaimsPrincipal user, LearnerHandlers h, CancellationToken ct) =>
            ApiResults.From(await h.GetAttemptAsync(id, user.UserId()!.Value, ct), Results.Ok));
        my.MapPut("/attempts/{id:guid}/answers", async (Guid id, SaveAnswersCommand command, ClaimsPrincipal user, LearnerHandlers h, CancellationToken ct) =>
            ApiResults.From(await h.SaveAsync(id, user.UserId()!.Value, command, ct), Results.Ok));
        my.MapPost("/attempts/{id:guid}/events", async (Guid id, AttemptEventCommand command, ClaimsPrincipal user, LearnerHandlers h, CancellationToken ct) =>
            ApiResults.From(await h.LogEventAsync(id, user.UserId()!.Value, command, ct), _ => Results.NoContent()));
        my.MapPost("/attempts/{id:guid}/next-section", async (Guid id, ClaimsPrincipal user, LearnerHandlers h, CancellationToken ct) =>
            ApiResults.From(await h.NextSectionAsync(id, user.UserId()!.Value, ct), Results.Ok));
        my.MapPost("/attempts/{id:guid}/submit", async (Guid id, ClaimsPrincipal user, LearnerHandlers h, CancellationToken ct) =>
            ApiResults.From(await h.SubmitAsync(id, user.UserId()!.Value, ct), Results.Ok));
        my.MapGet("/attempts/{id:guid}/result", async (Guid id, ClaimsPrincipal user, LearnerHandlers h, CancellationToken ct) =>
            ApiResults.From(await h.GetResultAsync(id, user.UserId()!.Value, ct), Results.Ok));
    }

    private static Actor Actor(ClaimsPrincipal user) => new(user.UserId()!.Value, user.IsInRole(StaffRoles.Teacher));
}
