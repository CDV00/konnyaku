using Konnyaku.Academic.Contracts;
using Konnyaku.Application.Authorization;
using Konnyaku.Application.Messaging;
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

        services.AddScoped<IDispatchJob, SendJob>();
        services.AddScoped<IDispatchJob, SessionReminderJob>();
        services.AddScoped<IDispatchJob>(sp => sp.GetRequiredService<LearnerWelcome>());
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
    }
}
