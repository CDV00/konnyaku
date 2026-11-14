using Konnyaku.Crm.Application;
using Konnyaku.Crm.Infrastructure;
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
            .RequireAuthorization(Policies.Staff);

        group.MapGet("/students", async (CrmQueries queries, CancellationToken ct) =>
                Results.Ok(await queries.ListStudentsAsync(ct)))
            .RequireAuthorization(Policies.Staff);

        group.MapPost("/students", async (RegisterStudentCommand command, RegisterStudentHandler handler, CancellationToken ct) =>
                ApiResults.From(await handler.HandleAsync(command, ct), s => Results.Created($"students/{s.Id}", s)))
            .RequireAuthorization(Policies.Staff);
    }
}
