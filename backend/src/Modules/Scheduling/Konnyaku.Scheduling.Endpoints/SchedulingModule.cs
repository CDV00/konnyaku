using System.Security.Claims;
using Konnyaku.Academic.Contracts;
using Konnyaku.Application.Authorization;
using Konnyaku.Application.Messaging;
using Konnyaku.Infrastructure.Auditing;
using Konnyaku.Infrastructure.Auth;
using Konnyaku.Infrastructure.Http;
using Konnyaku.Infrastructure.Messaging;
using Konnyaku.Infrastructure.Modules;
using Konnyaku.Learning.Contracts;
using Konnyaku.Scheduling.Application;
using Konnyaku.Scheduling.Contracts;
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
        services.AddScoped<IIntegrationEventHandler<EnrollmentActivatedV1>, EnrollmentActivatedBookingConsumer>();
        services.AddScoped<IIntegrationEventHandler<EnrollmentDroppedV1>, EnrollmentDroppedBookingConsumer>();
        services.AddScoped<IIntegrationEventHandler<EnrollmentFrozenV1>, EnrollmentFrozenBookingConsumer>();
        services.AddScoped<IIntegrationEventHandler<EnrollmentUnfrozenV1>, EnrollmentUnfrozenBookingConsumer>();
        services.AddScoped<IIntegrationEventHandler<AttendanceConfirmedV1>, AttendanceConfirmedBookingConsumer>();
        services.AddScoped<IIntegrationEventHandler<SessionSeatReleasedV1>, SessionSeatReleasedConsumer>();
    }

    public void AddWorkerServices(IServiceCollection services)
    {
        services.AddOutboxProcessor<SchedulingDbContext>();
        services.AddSchedulingWorker();
    }

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

        MapLearnerBookings(group.MapGroup("/my").RequireAuthorization(Policies.Learn));
        MapMakeupAdmin(group);
    }

    /// <summary>The learner's own seats: leave, make-ups, online/offline, flexible classes, waitlist.</summary>
    private static void MapLearnerBookings(RouteGroupBuilder my)
    {
        static IResult Id(Guid id) => Results.Ok(new { id });

        my.MapGet("/bookings", async (DateTimeOffset? from, DateTimeOffset? to, ClaimsPrincipal user, BookingQueries queries, CancellationToken ct) =>
        {
            var start = from ?? DateTimeOffset.UtcNow.AddHours(-12);
            return Results.Ok(await queries.ListMyBookingsAsync(user.UserId()!.Value, start, to ?? start.Add(DefaultWindow), ct));
        });
        my.MapPost("/bookings/{id:guid}/postpone", async (Guid id, ClaimsPrincipal user, LearnerBookingHandlers handlers, CancellationToken ct) =>
            ApiResults.From(await handlers.PostponeAsync(user.UserId()!.Value, id, ct), Results.Ok));
        my.MapPost("/bookings/{id:guid}/cancel", async (Guid id, ClaimsPrincipal user, LearnerBookingHandlers handlers, CancellationToken ct) =>
            ApiResults.From(await handlers.CancelAsync(user.UserId()!.Value, id, ct), Id));
        my.MapPost("/bookings/{id:guid}/mode", async (Guid id, SwitchModeCommand command, ClaimsPrincipal user, LearnerBookingHandlers handlers, CancellationToken ct) =>
            ApiResults.From(await handlers.SwitchModeAsync(user.UserId()!.Value, id, command, ct), Id));

        my.MapGet("/makeups", async (ClaimsPrincipal user, BookingQueries queries, CancellationToken ct) =>
            Results.Ok(await queries.ListMyEntitlementsAsync(user.UserId()!.Value, ct)));
        my.MapGet("/makeups/{id:guid}/options", async (Guid id, ClaimsPrincipal user, BookingQueries queries, CancellationToken ct) =>
            await queries.MakeupOptionsAsync(user.UserId()!.Value, id, ct) is { } options
                ? Results.Ok(options)
                : ApiResults.Error(Error.NotFound("scheduling.entitlement_not_found")));
        my.MapPost("/makeups/{id:guid}/book", async (Guid id, BookSessionCommand command, ClaimsPrincipal user, LearnerBookingHandlers handlers, CancellationToken ct) =>
            ApiResults.From(await handlers.BookMakeupAsync(user.UserId()!.Value, id, command, ct), Id));
        my.MapPost("/makeups/{id:guid}/request", async (Guid id, MakeupRequestCommand command, ClaimsPrincipal user, LearnerBookingHandlers handlers, CancellationToken ct) =>
            ApiResults.From(await handlers.RequestMakeupAsync(user.UserId()!.Value, id, command, ct), Id));

        my.MapGet("/flexible", async (ClaimsPrincipal user, BookingQueries queries, CancellationToken ct) =>
            Results.Ok(await queries.FlexibleAsync(user.UserId()!.Value, ct)));
        my.MapPost("/sessions/{id:guid}/book", async (Guid id, BookFlexibleCommand command, ClaimsPrincipal user, LearnerBookingHandlers handlers, CancellationToken ct) =>
            ApiResults.From(await handlers.BookFlexibleAsync(user.UserId()!.Value, id, command, ct), Id));
        my.MapPost("/sessions/{id:guid}/waitlist", async (Guid id, JoinWaitlistCommand command, ClaimsPrincipal user, LearnerBookingHandlers handlers, CancellationToken ct) =>
            ApiResults.From(await handlers.JoinWaitlistAsync(user.UserId()!.Value, id, command, ct), Id));
        my.MapGet("/waitlist", async (ClaimsPrincipal user, BookingQueries queries, CancellationToken ct) =>
            Results.Ok(await queries.ListMyWaitlistAsync(user.UserId()!.Value, ct)));
        my.MapPost("/waitlist/{id:guid}/accept", async (Guid id, ClaimsPrincipal user, LearnerBookingHandlers handlers, CancellationToken ct) =>
            ApiResults.From(await handlers.AcceptOfferAsync(user.UserId()!.Value, id, ct), Id));
        my.MapPost("/waitlist/{id:guid}/withdraw", async (Guid id, ClaimsPrincipal user, LearnerBookingHandlers handlers, CancellationToken ct) =>
            ApiResults.From(await handlers.WithdrawAsync(user.UserId()!.Value, id, ct), Id));
    }

    /// <summary>Coordinators and branch managers: exceptional make-ups, learners who postpone often, the center's booking rules.</summary>
    private static void MapMakeupAdmin(RouteGroupBuilder group)
    {
        static IResult Id(Guid id) => Results.Ok(new { id });

        var staff = group.MapGroup("").RequirePermission(Permissions.Schedules);
        staff.MapGet("/makeups/requests", async (BookingQueries queries, CancellationToken ct) =>
            Results.Ok(await queries.ListMakeupRequestsAsync(ct)));
        staff.MapGet("/makeups/{id:guid}/options", async (Guid id, BookingQueries queries, CancellationToken ct) =>
            await queries.StaffOptionsAsync(id, ct) is { } options
                ? Results.Ok(options)
                : ApiResults.Error(Error.NotFound("scheduling.entitlement_not_found")));
        staff.MapPost("/makeups/{id:guid}/place", async (Guid id, PlaceMakeupCommand command, ClaimsPrincipal user, MakeupAdminHandlers handlers, CancellationToken ct) =>
            ApiResults.From(await handlers.PlaceAsync(user.UserId()!.Value, id, command, ct), Id));
        staff.MapPost("/makeups/{id:guid}/decline", async (Guid id, MakeupAdminHandlers handlers, CancellationToken ct) =>
            ApiResults.From(await handlers.DeclineAsync(id, ct), Id));
        staff.MapGet("/postpones/frequent", async (int? days, int? min, BookingQueries queries, CancellationToken ct) =>
            Results.Ok(await queries.FrequentPostponesAsync(days ?? 60, min ?? 2, ct)));
        staff.MapGet("/settings/booking", async (MakeupAdminHandlers handlers, CancellationToken ct) => Results.Ok(await handlers.GetSettingsAsync(ct)));

        group.MapPut("/settings/booking", async (BookingSettingsView command, MakeupAdminHandlers handlers, CancellationToken ct) =>
                ApiResults.From(await handlers.SetSettingsAsync(command, ct), Results.Ok))
            .RequireAuthorization(Policies.TenantAdmin);
    }
}
