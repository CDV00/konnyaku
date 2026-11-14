using Konnyaku.Academic.Application;
using Konnyaku.Academic.Infrastructure;
using Konnyaku.Infrastructure.Auth;
using Konnyaku.Infrastructure.Http;
using Konnyaku.Infrastructure.Messaging;
using Konnyaku.Infrastructure.Modules;
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
        staff.MapGet("/classes", async (AcademicQueries queries, CancellationToken ct) =>
            Results.Ok(await queries.ListClassesAsync(ct)));
        staff.MapGet("/classes/{id:guid}/roster", async (Guid id, AcademicQueries queries, CancellationToken ct) =>
            Results.Ok(await queries.GetRosterAsync(id, ct)));
        staff.MapPost("/classes/{id:guid}/enrollments", async (Guid id, EnrollCommand command, EnrollHandler handler, CancellationToken ct) =>
            ApiResults.From(await handler.HandleAsync(id, command, ct), e => Results.Created($"enrollments/{e.EnrollmentId}", e)));
        staff.MapPost("/enrollments/{id:guid}/freeze", async (Guid id, FreezeCommand command, EnrollmentLifecycleHandler handler, CancellationToken ct) =>
            ApiResults.From(await handler.FreezeAsync(id, command, ct), Results.Ok));
        staff.MapPost("/enrollments/{id:guid}/unfreeze", async (Guid id, EnrollmentLifecycleHandler handler, CancellationToken ct) =>
            ApiResults.From(await handler.UnfreezeAsync(id, ct), Results.Ok));
        staff.MapPost("/enrollments/{id:guid}/drop", async (Guid id, EnrollmentLifecycleHandler handler, CancellationToken ct) =>
            ApiResults.From(await handler.DropAsync(id, ct), Results.Ok));

        var admin = group.MapGroup("").RequireAuthorization(Policies.TenantAdmin);
        admin.MapPost("/programs", async (CreateProgramCommand command, CreateProgramHandler handler, CancellationToken ct) =>
            ApiResults.From(await handler.HandleAsync(command, ct), p => Results.Created($"programs/{p.Id}", p)));
        admin.MapPost("/programs/{id:guid}/levels", async (Guid id, AddCourseLevelCommand command, AddCourseLevelHandler handler, CancellationToken ct) =>
            ApiResults.From(await handler.HandleAsync(id, command, ct), l => Results.Created($"levels/{l.Id}", l)));
        admin.MapPost("/classes", async (CreateClassCommand command, CreateClassHandler handler, CancellationToken ct) =>
            ApiResults.From(await handler.HandleAsync(command, ct), c => Results.Created($"classes/{c.Id}", c)));
    }
}
