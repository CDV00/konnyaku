using Konnyaku.Crm.Application;
using Konnyaku.Crm.Infrastructure;
using Konnyaku.Application.Authorization;
using Konnyaku.Infrastructure.Auditing;
using Konnyaku.Infrastructure.Auth;
using Konnyaku.Infrastructure.Http;
using Konnyaku.Infrastructure.Messaging;
using Konnyaku.Infrastructure.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Konnyaku.Crm.Endpoints;

public sealed class CrmModule : IModule
{
    public string Name => "crm";

    public IReadOnlyList<Type> DbContextTypes => [typeof(CrmDbContext)];

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddCrmInfrastructure(configuration, Name);
        services.AddScoped<SubmitLeadHandler>();
        services.AddScoped<LeadQueries>();
        services.AddScoped<ChangeLeadStatusHandler>();
        services.AddScoped<RegisterStudentHandler>();
        services.AddScoped<CrmEditHandlers>();
    }

    public void AddWorkerServices(IServiceCollection services) => services.AddOutboxProcessor<CrmDbContext>();

    public void MapEndpoints(RouteGroupBuilder group)
    {
        // Public registration form of the tenant site (tenant from host). Always 202 so bots learn nothing.
        group.MapPost("/public/leads", async (SubmitLeadCommand command, SubmitLeadHandler handler, CancellationToken ct) =>
                ApiResults.From(await handler.HandleAsync(command, ct), _ => Results.Accepted()))
            .RequireRateLimiting(RateLimitPolicies.PublicForms);

        group.MapGet("/leads", async (bool? openOnly, LeadQueries queries, CancellationToken ct) =>
                Results.Ok(await queries.ListAsync(openOnly ?? true, ct)))
            .RequireAuthorization(Policies.Staff);

        group.MapPost("/leads/{id:guid}/status", async (Guid id, ChangeLeadStatusCommand command, ChangeLeadStatusHandler handler, CancellationToken ct) =>
                ApiResults.From(await handler.HandleAsync(id, command, ct), _ => Results.NoContent()))
            .RequirePermission(Permissions.Leads);
        group.MapPut("/leads/{id:guid}", async (Guid id, EditLeadCommand command, CrmEditHandlers handlers, CancellationToken ct) =>
                ApiResults.From(await handlers.EditLeadAsync(id, command, ct), _ => Results.NoContent()))
            .RequirePermission(Permissions.Leads);

        group.MapGet("/students", async (CrmQueries queries, CancellationToken ct) =>
                Results.Ok(await queries.ListStudentsAsync(ct)))
            .RequireAuthorization(Policies.Staff);

        group.MapPost("/students", async (RegisterStudentCommand command, RegisterStudentHandler handler, CancellationToken ct) =>
                ApiResults.From(await handler.HandleAsync(command, ct), s => Results.Created($"students/{s.Id}", s)))
            .RequirePermission(Permissions.Students);
        group.MapGet("/students/details", async (CrmQueries queries, CancellationToken ct) =>
                Results.Ok(await queries.ListStudentDetailsAsync(ct)))
            .RequirePermission(Permissions.Students);
        group.MapPut("/students/{id:guid}", async (Guid id, UpdateStudentCommand command, CrmEditHandlers handlers, CancellationToken ct) =>
                ApiResults.From(await handlers.UpdateStudentAsync(id, command, ct), Results.Ok))
            .RequirePermission(Permissions.Students);
        group.MapPost("/students/{id:guid}/deactivate", async (Guid id, CrmEditHandlers handlers, CancellationToken ct) =>
                ApiResults.From(await handlers.SetStudentActiveAsync(id, false, ct), Results.Ok))
            .RequirePermission(Permissions.Students);
        group.MapPost("/students/{id:guid}/activate", async (Guid id, CrmEditHandlers handlers, CancellationToken ct) =>
                ApiResults.From(await handlers.SetStudentActiveAsync(id, true, ct), Results.Ok))
            .RequirePermission(Permissions.Students);

        // Student and lead history holds old and new contact details: same audience as the records themselves.
        group.MapChangeHistory<CrmDbContext>().RequirePermission(Permissions.Students, Permissions.Leads);
    }
}
