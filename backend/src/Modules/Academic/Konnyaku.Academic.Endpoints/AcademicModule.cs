using System.Security.Claims;
using Konnyaku.Academic.Application;
using Konnyaku.Academic.Infrastructure;
using Konnyaku.Application.Authorization;
using Konnyaku.Infrastructure.Auditing;
using Konnyaku.Infrastructure.Auth;
using Konnyaku.Infrastructure.Http;
using Konnyaku.Infrastructure.Messaging;
using Konnyaku.Infrastructure.Modules;
using Konnyaku.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Konnyaku.Academic.Endpoints;

public sealed class AcademicModule : IModule
{
    public string Name => "academic";

    public IReadOnlyList<Type> DbContextTypes => [typeof(AcademicDbContext)];

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddAcademicInfrastructure(configuration, Name);
        services.AddScoped<CreateProgramHandler>();
        services.AddScoped<AddCourseLevelHandler>();
        services.AddScoped<CreateClassHandler>();
        services.AddScoped<EnrollHandler>();
        services.AddScoped<EnrollmentLifecycleHandler>();
        services.AddScoped<ProgramAdminHandler>();
        services.AddScoped<ClassAdminHandler>();
        services.AddScoped<ClassAccess>();
        services.AddScoped<Konnyaku.Application.Messaging.IIntegrationEventHandler<Konnyaku.Billing.Contracts.OrderPaidV1>, OrderPaidConsumer>();
    }

    public void AddWorkerServices(IServiceCollection services) => services.AddOutboxProcessor<AcademicDbContext>();

    public void MapEndpoints(RouteGroupBuilder group)
    {
        // Public course catalogue of the tenant site.
        group.MapGet("/public/programs", async (string? language, AcademicQueries queries, CancellationToken ct) =>
            Results.Ok(await queries.ListProgramsAsync(publicOnly: true, language, ct)));

        var staff = group.MapGroup("").RequireAuthorization(Policies.Staff);
        staff.MapGet("/programs", async (string? language, AcademicQueries queries, CancellationToken ct) =>
            Results.Ok(await queries.ListProgramsAsync(publicOnly: false, language, ct)));
        // Every class for members who run or sell them; a teacher sees the classes they teach.
        staff.MapGet("/classes", async (ClaimsPrincipal user, ClassAccess classes, CancellationToken ct) =>
            Results.Ok(await classes.VisibleClassesAsync(user.UserId()!.Value, ct)));
        staff.MapGet("/classes/{id:guid}/roster", async (Guid id, ClaimsPrincipal user, ClassAccess classes, AcademicQueries queries, CancellationToken ct) =>
            await classes.CanSeeAsync(id, user.UserId()!.Value, ct)
                ? Results.Ok(await queries.GetRosterAsync(id, ct))
                : ApiResults.Error(Error.Forbidden("access.forbidden")));

        // Hand-made enrollments: the class's branch must be one the member runs.
        staff.MapPost("/classes/{id:guid}/enrollments", async (Guid id, EnrollCommand command, ClassAccess classes, EnrollHandler handler, CancellationToken ct) =>
                await classes.EnsureEnrollmentBranchAsync(id, null, ct) is { } denied
                    ? ApiResults.Error(denied)
                    : ApiResults.From(await handler.HandleAsync(id, command, ct), e => Results.Created($"enrollments/{e.EnrollmentId}", e)))
            .RequirePermission(Permissions.Classes);
        staff.MapPost("/enrollments/{id:guid}/freeze", async (Guid id, FreezeCommand command, ClassAccess classes, EnrollmentLifecycleHandler handler, CancellationToken ct) =>
                await classes.EnsureEnrollmentBranchAsync(null, id, ct) is { } denied ? ApiResults.Error(denied) : ApiResults.From(await handler.FreezeAsync(id, command, ct), Results.Ok))
            .RequirePermission(Permissions.Classes);
        staff.MapPost("/enrollments/{id:guid}/unfreeze", async (Guid id, ClassAccess classes, EnrollmentLifecycleHandler handler, CancellationToken ct) =>
                await classes.EnsureEnrollmentBranchAsync(null, id, ct) is { } denied ? ApiResults.Error(denied) : ApiResults.From(await handler.UnfreezeAsync(id, ct), Results.Ok))
            .RequirePermission(Permissions.Classes);
        staff.MapPost("/enrollments/{id:guid}/drop", async (Guid id, ClassAccess classes, EnrollmentLifecycleHandler handler, CancellationToken ct) =>
                await classes.EnsureEnrollmentBranchAsync(null, id, ct) is { } denied ? ApiResults.Error(denied) : ApiResults.From(await handler.DropAsync(id, ct), Results.Ok))
            .RequirePermission(Permissions.Classes);

        var programs = group.MapGroup("").RequirePermission(Permissions.Programs);
        programs.MapPost("/programs", async (CreateProgramCommand command, CreateProgramHandler handler, CancellationToken ct) =>
            ApiResults.From(await handler.HandleAsync(command, ct), p => Results.Created($"programs/{p.Id}", p)));
        programs.MapPut("/programs/{id:guid}", async (Guid id, UpdateProgramCommand command, ProgramAdminHandler handler, CancellationToken ct) =>
            ApiResults.From(await handler.UpdateAsync(id, command, ct), Results.Ok));
        programs.MapPost("/programs/{id:guid}/deactivate", async (Guid id, ProgramAdminHandler handler, CancellationToken ct) =>
            ApiResults.From(await handler.SetActiveAsync(id, false, ct), Results.Ok));
        programs.MapPost("/programs/{id:guid}/activate", async (Guid id, ProgramAdminHandler handler, CancellationToken ct) =>
            ApiResults.From(await handler.SetActiveAsync(id, true, ct), Results.Ok));
        programs.MapPost("/programs/{id:guid}/levels", async (Guid id, AddCourseLevelCommand command, AddCourseLevelHandler handler, CancellationToken ct) =>
            ApiResults.From(await handler.HandleAsync(id, command, ct), l => Results.Created($"levels/{l.Id}", l)));
        programs.MapPut("/levels/{id:guid}", async (Guid id, UpdateCourseLevelCommand command, ProgramAdminHandler handler, CancellationToken ct) =>
            ApiResults.From(await handler.UpdateLevelAsync(id, command, ct), Results.Ok));
        programs.MapPost("/levels/{id:guid}/deactivate", async (Guid id, ProgramAdminHandler handler, CancellationToken ct) =>
            ApiResults.From(await handler.SetLevelActiveAsync(id, false, ct), Results.Ok));
        programs.MapPost("/levels/{id:guid}/activate", async (Guid id, ProgramAdminHandler handler, CancellationToken ct) =>
            ApiResults.From(await handler.SetLevelActiveAsync(id, true, ct), Results.Ok));

        // Classes: branch managers and coordinators too, for their branches (checked in the handlers).
        var classes = group.MapGroup("").RequirePermission(Permissions.Classes);
        classes.MapPost("/classes", async (CreateClassCommand command, CreateClassHandler handler, CancellationToken ct) =>
            ApiResults.From(await handler.HandleAsync(command, ct), c => Results.Created($"classes/{c.Id}", c)));
        classes.MapPut("/classes/{id:guid}", async (Guid id, UpdateClassCommand command, ClassAdminHandler handler, CancellationToken ct) =>
            ApiResults.From(await handler.UpdateAsync(id, command, ct), Results.Ok));
        classes.MapPost("/classes/{id:guid}/close", async (Guid id, CloseClassCommand command, ClassAdminHandler handler, CancellationToken ct) =>
            ApiResults.From(await handler.CloseAsync(id, command, ct), Results.Ok));
        classes.MapPost("/classes/{id:guid}/reopen", async (Guid id, ClassAdminHandler handler, CancellationToken ct) =>
            ApiResults.From(await handler.ReopenAsync(id, ct), Results.Ok));

        group.MapChangeHistory<AcademicDbContext>(Permissions.Programs, Permissions.Classes, Permissions.ClassesView);
    }
}
