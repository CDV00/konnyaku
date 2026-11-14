using System.Security.Claims;
using Konnyaku.Infrastructure.Auth;
using Konnyaku.Infrastructure.Http;
using Konnyaku.Infrastructure.Messaging;
using Konnyaku.Infrastructure.Modules;
using Konnyaku.Scheduling.Application;
using Konnyaku.Scheduling.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Konnyaku.Scheduling.Endpoints;

public sealed class SchedulingModule : IModule
{
    private static readonly TimeSpan DefaultWindow = TimeSpan.FromDays(60);

    public string Name => "scheduling";

    public IReadOnlyList<Type> DbContextTypes => [typeof(SchedulingDbContext)];

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSchedulingInfrastructure(configuration, Name);
        services.AddScoped<ResourceValidator>();
        services.AddScoped<ScheduleClassHandler>();
        services.AddScoped<SessionChangeHandler>();
    }

    public void AddWorkerServices(IServiceCollection services) => services.AddOutboxProcessor<SchedulingDbContext>();

    public void MapEndpoints(RouteGroupBuilder group)
    {
        var coordinators = group.MapGroup("").RequireAuthorization(p => p.RequireAuthenticatedUser()
            .RequireClaim(KonnyakuClaims.Audience, Audiences.Staff)
            .RequireRole("TenantAdmin", "Coordinator"));

        coordinators.MapPost("/classes/{classId:guid}/schedule", async (Guid classId, ScheduleClassCommand command, ScheduleClassHandler handler, CancellationToken ct) =>
            ApiResults.From(await handler.HandleAsync(classId, command, ct), s => Results.Created($"classes/{classId}/sessions", s)));
        coordinators.MapPost("/sessions/{id:guid}/reschedule", async (Guid id, RescheduleCommand command, SessionChangeHandler handler, CancellationToken ct) =>
            ApiResults.From(await handler.RescheduleAsync(id, command, ct), Results.Ok));
        coordinators.MapPost("/sessions/{id:guid}/teacher", async (Guid id, AssignTeacherCommand command, SessionChangeHandler handler, CancellationToken ct) =>
            ApiResults.From(await handler.AssignTeacherAsync(id, command, ct), Results.Ok));
        coordinators.MapPost("/sessions/{id:guid}/cancel", async (Guid id, CancelSessionCommand command, SessionChangeHandler handler, CancellationToken ct) =>
            ApiResults.From(await handler.CancelAsync(id, command, ct), Results.Ok));

        group.MapGet("/classes/{classId:guid}/sessions", async (Guid classId, SchedulingQueries queries, CancellationToken ct) =>
                Results.Ok(await queries.ListClassSessionsAsync(classId, ct)))
            .RequireAuthorization(Policies.Staff);

        // Teacher app: own teaching schedule.
        group.MapGet("/my/teaching", async (DateTimeOffset? from, DateTimeOffset? to, ClaimsPrincipal user, SchedulingQueries queries, CancellationToken ct) =>
            {
                var start = from ?? DateTimeOffset.UtcNow.AddHours(-12);
                return Results.Ok(await queries.ListTeacherSessionsAsync(user.UserId()!.Value, start, to ?? start.Add(DefaultWindow), ct));
            })
            .RequireAuthorization(Policies.Staff);

        // Learner portal: sessions of the learner's classes.
        group.MapGet("/my/sessions", async (DateTimeOffset? from, DateTimeOffset? to, ClaimsPrincipal user, SchedulingQueries queries, CancellationToken ct) =>
            {
                var start = from ?? DateTimeOffset.UtcNow.AddHours(-12);
                return Results.Ok(await queries.ListLearnerSessionsAsync(user.UserId()!.Value, start, to ?? start.Add(DefaultWindow), ct));
            })
            .RequireAuthorization(Policies.Learn);
    }
}
