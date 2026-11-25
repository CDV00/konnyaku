using System.Security.Claims;
using Konnyaku.Academic.Contracts;
using Konnyaku.Application.Authorization;
using Konnyaku.Application.Messaging;
using Konnyaku.Assessment.Contracts;
using Konnyaku.Billing.Contracts;
using Konnyaku.Crm.Contracts;
using Konnyaku.Hr.Contracts;
using Konnyaku.Identity.Contracts;
using Konnyaku.Infrastructure.Auth;
using Konnyaku.Infrastructure.Http;
using Konnyaku.Infrastructure.Messaging;
using Konnyaku.Infrastructure.Modules;
using Konnyaku.Notification.Application;
using Konnyaku.Notification.Contracts;
using Konnyaku.Notification.Infrastructure;
using Konnyaku.Scheduling.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Konnyaku.Notification.Endpoints;

public sealed class NotificationModule : IModule
{
    public string Name => "notification";

    public IReadOnlyList<Type> DbContextTypes => [typeof(NotificationDbContext)];

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddNotificationInfrastructure(configuration, Name);

        services.AddScoped<SessionAudience>();
        services.AddScoped<SessionReminderPlanner>();
        services.AddScoped<LearnerWelcome>();
        services.AddScoped<IIntegrationEventHandler<LeadCreatedV1>, LeadCreatedNotifier>();
        services.AddScoped<IIntegrationEventHandler<StudentAnonymizedV1>, StudentAnonymizedEraser>();
        services.AddScoped<IIntegrationEventHandler<LearnerAccountCreatedV1>>(sp => sp.GetRequiredService<LearnerWelcome>());
        services.AddScoped<IIntegrationEventHandler<CreditLowV1>, CreditLowNotifier>();
        services.AddScoped<IIntegrationEventHandler<EnrollmentActivatedV1>, EnrollmentActivatedNotifier>();
        services.AddScoped<IIntegrationEventHandler<SessionScheduledV1>>(sp => sp.GetRequiredService<SessionReminderPlanner>());
        services.AddScoped<IIntegrationEventHandler<SessionRescheduledV1>, SessionRescheduledNotifier>();
        services.AddScoped<IIntegrationEventHandler<SessionCancelledV1>, SessionCancelledNotifier>();
        services.AddScoped<IIntegrationEventHandler<PayrollPeriodLockedV1>, PayrollLockedNotifier>();
        services.AddScoped<IIntegrationEventHandler<NotificationRequestedV1>, NotificationRequestedConsumer>();
        services.AddScoped<IIntegrationEventHandler<WaitlistSeatOfferedV1>, WaitlistSeatOfferedNotifier>();
        services.AddScoped<IIntegrationEventHandler<MakeupApprovedV1>, MakeupApprovedNotifier>();
        services.AddScoped<AssignmentPlanner>();
        services.AddScoped<IIntegrationEventHandler<AssignmentPublishedV1>>(sp => sp.GetRequiredService<AssignmentPlanner>());
        services.AddScoped<IIntegrationEventHandler<AssignmentRescheduledV1>>(sp => sp.GetRequiredService<AssignmentPlanner>());
        services.AddScoped<IIntegrationEventHandler<AssessmentGradedV1>, AssessmentGradedNotifier>();

        services.AddScoped<IDispatchJob, SendJob>();
        services.AddScoped<IDispatchJob, SessionReminderJob>();
        services.AddScoped<IDispatchJob>(sp => sp.GetRequiredService<LearnerWelcome>());
        services.AddScoped<IDispatchJob, PushJob>();
        services.AddScoped<IDispatchJob, InboxVisibleJob>();
        services.AddScoped<IDispatchJob, AssignmentOpenJob>();
        services.AddScoped<IDispatchJob, AssignmentDueJob>();
    }

    public void AddWorkerServices(IServiceCollection services)
    {
        services.AddOutboxProcessor<NotificationDbContext>();
        services.AddHostedService<NotificationDispatchService>();
    }

    public void MapEndpoints(RouteGroupBuilder group)
    {
        var admin = group.MapGroup("").RequirePermission(Permissions.Notifications);

        admin.MapGet("/messages", async (string? status, int? take, NotificationLog log, CancellationToken ct) =>
            Results.Ok(await log.ListAsync(status, take, ct)));
        admin.MapPost("/messages/{id:guid}/resend", async (Guid id, NotificationLog log, CancellationToken ct) =>
            ApiResults.From(await log.ResendAsync(id, ct), _ => Results.NoContent()));

        // The signed-in person's own inbox, channel settings and browsers (learners and staff alike).
        var me = group.MapGroup("").RequireAuthorization();
        me.MapGet("/inbox", async (int? page, int? size, bool? unread, ClaimsPrincipal user, InboxHandlers h, CancellationToken ct) =>
            Results.Ok(await h.ListAsync(user.UserId()!.Value, page, size, unread == true, ct)));
        me.MapGet("/inbox/unread-count", async (ClaimsPrincipal user, InboxHandlers h, CancellationToken ct) =>
            Results.Ok(await h.CountAsync(user.UserId()!.Value, ct)));
        me.MapGet("/inbox/sync", async (long? since, ClaimsPrincipal user, InboxHandlers h, CancellationToken ct) =>
            Results.Ok(await h.SyncAsync(user.UserId()!.Value, Math.Max(since ?? 0, 0), ct)));
        me.MapPost("/inbox/{id:guid}/read", async (Guid id, ClaimsPrincipal user, InboxHandlers h, CancellationToken ct) =>
            ApiResults.From(await h.MarkReadAsync(user.UserId()!.Value, id, ct), Results.Ok));
        me.MapPost("/inbox/read-all", async (ClaimsPrincipal user, InboxHandlers h, CancellationToken ct) =>
            Results.Ok(await h.MarkAllReadAsync(user.UserId()!.Value, ct)));

        me.MapGet("/preferences", async (ClaimsPrincipal user, InboxHandlers h, CancellationToken ct) =>
            Results.Ok(await h.PreferencesAsync(user.UserId()!.Value, IsStaff(user), ct)));
        me.MapPut("/preferences", async (PreferenceCommand[] commands, ClaimsPrincipal user, InboxHandlers h, CancellationToken ct) =>
            ApiResults.From(await h.SetPreferencesAsync(user.UserId()!.Value, IsStaff(user), commands, ct), Results.Ok));

        me.MapGet("/push/config", (InboxHandlers h) => Results.Ok(h.PushConfig()));
        me.MapPost("/push/subscriptions", async (PushSubscribeCommand command, ClaimsPrincipal user, InboxHandlers h, CancellationToken ct) =>
            ApiResults.From(await h.SubscribeAsync(user.UserId()!.Value, command, ct), Results.Ok));
        me.MapDelete("/push/subscriptions/{id:guid}", async (Guid id, ClaimsPrincipal user, InboxHandlers h, CancellationToken ct) =>
        {
            await h.UnsubscribeAsync(user.UserId()!.Value, id, null, ct);
            return Results.NoContent();
        });
        me.MapDelete("/push/subscriptions", async (string? endpoint, ClaimsPrincipal user, InboxHandlers h, CancellationToken ct) =>
        {
            await h.UnsubscribeAsync(user.UserId()!.Value, null, endpoint, ct);
            return Results.NoContent();
        });

        // A fresh short-lived ticket for the realtime WebSocket (/rt), on every (re)connect of a tab.
        me.MapPost("/realtime/ticket", (ClaimsPrincipal user, RealtimeTickets tickets) =>
            tickets.Issue(user.UserId()!.Value, user.TenantId()!.Value) is { } ticket
                ? Results.Ok(ticket)
                : Results.Json(new { code = "notification.realtime_not_configured" }, statusCode: StatusCodes.Status503ServiceUnavailable));
    }

    private static bool IsStaff(ClaimsPrincipal user) => user.HasClaim(KonnyakuClaims.Audience, Audiences.Staff);
}
