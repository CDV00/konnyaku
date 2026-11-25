using System.Security.Claims;
using Konnyaku.Application.Authorization;
using Konnyaku.Infrastructure.Auditing;
using Konnyaku.Infrastructure.Auth;
using Konnyaku.Infrastructure.Http;
using Konnyaku.Infrastructure.Messaging;
using Konnyaku.Infrastructure.Modules;
using Konnyaku.Scheduling.Application;
using Konnyaku.Scheduling.Infrastructure;
using Konnyaku.SharedKernel;
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
        services.AddScoped<ReplanScheduleHandler>();
        services.AddScoped<Konnyaku.Application.Messaging.IIntegrationEventHandler<Konnyaku.Academic.Contracts.ClassDeliveryModeChangedV1>, ClassDeliveryModeChangedConsumer>();
        services.AddScoped<Konnyaku.Application.Messaging.IIntegrationEventHandler<Konnyaku.Academic.Contracts.ClassClosedV1>, ClassClosedConsumer>();
        services.AddScoped<Konnyaku.Application.Messaging.IIntegrationEventHandler<Konnyaku.Workspace.Contracts.MeetingProvisionedV1>, MeetingProvisionedConsumer>();
    }

    public void AddWorkerServices(IServiceCollection services) => services.AddOutboxProcessor<SchedulingDbContext>();

    public void MapEndpoints(RouteGroupBuilder group)
    {
        // Branch managers and coordinators too, for classes of their branches (checked in the handlers).
        var coordinators = group.MapGroup("").RequirePermission(Permissions.Schedules);

        coordinators.MapPost("/classes/{classId:guid}/schedule", async (Guid classId, ScheduleClassCommand command, ScheduleClassHandler handler, CancellationToken ct) =>
            ApiResults.From(await handler.HandleAsync(classId, command, ct), s => Results.Created($"classes/{classId}/sessions", s)));
        coordinators.MapPut("/classes/{classId:guid}/schedule", async (Guid classId, ReplanScheduleCommand command, ReplanScheduleHandler handler, CancellationToken ct) =>
            ApiResults.From(await handler.HandleAsync(classId, command, ct), Results.Ok));
        coordinators.MapPost("/sessions/{id:guid}/reschedule", async (Guid id, RescheduleCommand command, SessionChangeHandler handler, CancellationToken ct) =>
            ApiResults.From(await handler.RescheduleAsync(id, command, ct), Results.Ok));
        coordinators.MapPost("/sessions/{id:guid}/teacher", async (Guid id, AssignTeacherCommand command, SessionChangeHandler handler, CancellationToken ct) =>
            ApiResults.From(await handler.AssignTeacherAsync(id, command, ct), Results.Ok));
        coordinators.MapPost("/sessions/{id:guid}/cancel", async (Guid id, CancelSessionCommand command, SessionChangeHandler handler, CancellationToken ct) =>
            ApiResults.From(await handler.CancelAsync(id, command, ct), Results.Ok));

        // Members who see every class (or schedule them); a teacher only the classes they teach.
        static async Task<bool> CanSeeAsync(Guid classId, ClaimsPrincipal user, IAccessControl access, SchedulingQueries queries, CancellationToken ct) =>
            await access.HasAsync(Permissions.ClassesView, ct) || await access.HasAsync(Permissions.Schedules, ct)
            || (await queries.ListClassIdsOfTeacherAsync(user.UserId()!.Value, ct)).Contains(classId);

        group.MapGet("/classes/{classId:guid}/pattern", async (Guid classId, ClaimsPrincipal user, IAccessControl access, SchedulingQueries queries, CancellationToken ct) =>
                !await CanSeeAsync(classId, user, access, queries, ct) ? ApiResults.Error(Error.Forbidden("access.forbidden"))
                : await queries.GetPatternAsync(classId, ct) is { } pattern ? Results.Ok(pattern) : Results.NoContent())
            .RequireAuthorization(Policies.Staff);

        group.MapChangeHistory<SchedulingDbContext>(Permissions.Schedules, Permissions.ClassesView);

        group.MapGet("/classes/{classId:guid}/sessions", async (Guid classId, ClaimsPrincipal user, IAccessControl access, SchedulingQueries queries, CancellationToken ct) =>
                await CanSeeAsync(classId, user, access, queries, ct)
                    ? Results.Ok(await queries.ListClassSessionsAsync(classId, ct))
                    : ApiResults.Error(Error.Forbidden("access.forbidden")))
            .RequireAuthorization(Policies.Staff);

        // Teacher app: own teaching schedule, including the past week (attendance is taken after the lesson).
        group.MapGet("/my/teaching", async (DateTimeOffset? from, DateTimeOffset? to, ClaimsPrincipal user, SchedulingQueries queries, CancellationToken ct) =>
            {
                var start = from ?? DateTimeOffset.UtcNow.AddDays(-7);
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
