using System.Security.Claims;
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
        services.AddSingleton(configuration.GetSection(CaptchaOptions.Section).Get<CaptchaOptions>() ?? new CaptchaOptions());
        services.AddScoped<CaptchaService>();
        services.AddScoped<SubmitLeadHandler>();
        services.AddScoped<ChangeLeadStatusHandler>();
        services.AddScoped<RegisterStudentHandler>();
        services.AddScoped<CrmEditHandlers>();
    }

    public void AddWorkerServices(IServiceCollection services)
    {
        services.AddOutboxProcessor<CrmDbContext>();
        services.AddCrmWorker();
    }

    public void MapEndpoints(RouteGroupBuilder group)
    {
        // Proof-of-work challenge for the public form (tenant from host); 204 when the captcha is switched off.
        group.MapGet("/public/captcha", (CaptchaService captcha) =>
            captcha.Enabled ? ApiResults.From(captcha.Issue(), Results.Ok) : Results.NoContent());

        // Public registration form of the tenant site (tenant from host). Always 202 so bots learn nothing.
        group.MapPost("/public/leads", async (SubmitLeadCommand command, SubmitLeadHandler handler, CancellationToken ct) =>
                ApiResults.From(await handler.HandleAsync(command, ct), _ => Results.Accepted()))
            .RequireRateLimiting(RateLimitPolicies.PublicForms);

        // Lists: filtered, sorted, paged; contact details masked. `openOnly=false` (older callers) means every status.
        group.MapGet("/leads", async ([AsParameters] LeadFilter filter, [AsParameters] ListParams list, bool? openOnly, CrmListQueries queries,
                    CancellationToken ct) =>
                Results.Ok(await queries.ListLeadsAsync(openOnly == false && filter.Status is null ? filter with { Status = "all" } : filter,
                    list.ToRequest(CrmListQueries.LeadSorts), ct)))
            .RequirePermission(Permissions.Leads);
        group.MapPost("/leads/{id:guid}/reveal", async (Guid id, RevealCommand? command, HttpContext http, RevealHandlers handlers, CancellationToken ct) =>
                ApiResults.From(await handlers.RevealLeadAsync(id, command ?? new RevealCommand(null), Actor(http), ct), Results.Ok))
            .RequirePermission(Permissions.Leads);
        group.MapGet("/leads/export", async ([AsParameters] LeadFilter filter, string? locale, HttpContext http, ExportHandlers exports, CancellationToken ct) =>
                ApiResults.From(await exports.ExportLeadsAsync(filter, locale, Actor(http), ct), File))
            .RequirePermission(Permissions.PersonalDataExport);
        group.MapGet("/leads/print", async ([AsParameters] LeadFilter filter, HttpContext http, ExportHandlers exports, CancellationToken ct) =>
                ApiResults.From(await exports.PrintLeadsAsync(filter, Actor(http), ct), Results.Ok))
            .RequirePermission(Permissions.PersonalDataExport);

        group.MapPost("/leads/{id:guid}/status", async (Guid id, ChangeLeadStatusCommand command, ChangeLeadStatusHandler handler, CancellationToken ct) =>
                ApiResults.From(await handler.HandleAsync(id, command, ct), _ => Results.NoContent()))
            .RequirePermission(Permissions.Leads);
        group.MapPut("/leads/{id:guid}", async (Guid id, EditLeadCommand command, CrmEditHandlers handlers, CancellationToken ct) =>
                ApiResults.From(await handlers.EditLeadAsync(id, command, ct), _ => Results.NoContent()))
            .RequirePermission(Permissions.Leads);

        // Teachers do not list students: their class rosters and attendance carry the names they need.
        group.MapGet("/students", async ([AsParameters] StudentFilter filter, [AsParameters] ListParams list, CrmListQueries queries, CancellationToken ct) =>
                Results.Ok(await queries.ListStudentsAsync(filter, list.ToRequest(CrmListQueries.StudentSorts), ct)))
            .RequirePermission(Permissions.StudentsView);
        group.MapPost("/students/{id:guid}/reveal", async (Guid id, RevealCommand? command, HttpContext http, RevealHandlers handlers, CancellationToken ct) =>
                ApiResults.From(await handlers.RevealStudentAsync(id, command ?? new RevealCommand(null), Actor(http), ct), Results.Ok))
            .RequirePermission(Permissions.Students);
        group.MapGet("/students/export", async ([AsParameters] StudentFilter filter, string? locale, HttpContext http, ExportHandlers exports, CancellationToken ct) =>
                ApiResults.From(await exports.ExportStudentsAsync(filter, locale, Actor(http), ct), File))
            .RequirePermission(Permissions.PersonalDataExport);
        group.MapGet("/students/print", async ([AsParameters] StudentFilter filter, HttpContext http, ExportHandlers exports, CancellationToken ct) =>
                ApiResults.From(await exports.PrintStudentsAsync(filter, Actor(http), ct), Results.Ok))
            .RequirePermission(Permissions.PersonalDataExport);

        group.MapPost("/students", async (RegisterStudentCommand command, RegisterStudentHandler handler, CancellationToken ct) =>
                ApiResults.From(await handler.HandleAsync(command, ct), s => Results.Created($"students/{s.Id}", s)))
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

        // Data subject requests, subject export, erasure and the access log: the tenant admin.
        var privacy = group.MapGroup("/privacy");
        privacy.MapGet("/requests", async ([AsParameters] RequestFilter filter, [AsParameters] ListParams list, PrivacyHandlers handlers, CancellationToken ct) =>
                Results.Ok(await handlers.ListRequestsAsync(filter, list.ToRequest(), ct)))
            .RequirePermission(Permissions.Privacy);
        privacy.MapPost("/requests", async (CreateDataSubjectRequestCommand command, PrivacyHandlers handlers, CancellationToken ct) =>
                ApiResults.From(await handlers.CreateRequestAsync(command, ct), id => Results.Created($"privacy/requests/{id}", new { id })))
            .RequirePermission(Permissions.Privacy);
        privacy.MapPost("/requests/{id:guid}/resolve", async (Guid id, ResolveDataSubjectRequestCommand command, HttpContext http, PrivacyHandlers handlers, CancellationToken ct) =>
                ApiResults.From(await handlers.ResolveRequestAsync(id, command, Actor(http), ct), _ => Results.NoContent()))
            .RequirePermission(Permissions.Privacy);
        privacy.MapGet("/students/{id:guid}/export", async (Guid id, HttpContext http, PrivacyHandlers handlers, CancellationToken ct) =>
                ApiResults.From(await handlers.ExportStudentAsync(id, Actor(http), ct), File))
            .RequirePermission(Permissions.Privacy);
        privacy.MapGet("/leads/{id:guid}/export", async (Guid id, HttpContext http, PrivacyHandlers handlers, CancellationToken ct) =>
                ApiResults.From(await handlers.ExportLeadAsync(id, Actor(http), ct), File))
            .RequirePermission(Permissions.Privacy);
        privacy.MapPost("/students/{id:guid}/anonymize", async (Guid id, AnonymizeCommand? command, HttpContext http, PrivacyHandlers handlers, CancellationToken ct) =>
                ApiResults.From(await handlers.AnonymizeStudentAsync(id, command ?? new AnonymizeCommand(null), Actor(http), ct), _ => Results.NoContent()))
            .RequirePermission(Permissions.Privacy);
        privacy.MapPost("/leads/{id:guid}/anonymize", async (Guid id, AnonymizeCommand? command, HttpContext http, PrivacyHandlers handlers, CancellationToken ct) =>
                ApiResults.From(await handlers.AnonymizeLeadAsync(id, command ?? new AnonymizeCommand(null), Actor(http), ct), _ => Results.NoContent()))
            .RequirePermission(Permissions.Privacy);
        privacy.MapGet("/access-log", async ([AsParameters] AccessLogFilter filter, [AsParameters] ListParams list, PrivacyHandlers handlers, CancellationToken ct) =>
                Results.Ok(await handlers.ListAccessAsync(filter, list.ToRequest(), ct)))
            .RequirePermission(Permissions.Privacy);

        // Student and lead history holds (masked) contact changes: same audience as the records themselves.
        group.MapChangeHistory<CrmDbContext>(Permissions.Students, Permissions.Leads);
    }

    /// <summary>Who is acting, from the access token (never from the request body) and the connection.</summary>
    private static RequestActor Actor(HttpContext http) =>
        new(http.User.UserId(), http.User.FindFirstValue(KonnyakuClaims.Name), http.Connection.RemoteIpAddress?.ToString());

    private static IResult File(ExportFile file) => Results.File(file.Content, file.ContentType, file.FileName);
}
