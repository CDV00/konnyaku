using System.Security.Claims;
using Konnyaku.Application.Messaging;
using Konnyaku.Hr.Application;
using Konnyaku.Hr.Infrastructure;
using Konnyaku.Infrastructure.Auth;
using Konnyaku.Infrastructure.Http;
using Konnyaku.Infrastructure.Messaging;
using Konnyaku.Infrastructure.Modules;
using Konnyaku.Learning.Contracts;
using Konnyaku.Workspace.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Konnyaku.Hr.Endpoints;

public sealed class HrModule : IModule
{
    public string Name => "hr";

    public IReadOnlyList<Type> DbContextTypes => [typeof(HrDbContext)];

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddHrInfrastructure(configuration, Name);
        services.AddScoped<PayrollHandlers>();
        services.AddScoped<IIntegrationEventHandler<ParticipantsCollectedV1>, ParticipantsCollectedTimesheetConsumer>();
        services.AddScoped<IIntegrationEventHandler<SessionAttendanceConfirmedV1>, AttendanceConfirmedTimesheetConsumer>();
    }

    public void AddWorkerServices(IServiceCollection services) => services.AddOutboxProcessor<HrDbContext>();

    public void MapEndpoints(RouteGroupBuilder group)
    {
        var payroll = group.MapGroup("").RequireAuthorization(p => p.RequireAuthenticatedUser()
            .RequireClaim(KonnyakuClaims.Audience, Audiences.Staff)
            .RequireRole("TenantAdmin", "Accountant"));

        payroll.MapPut("/teachers/{teacherId:guid}/rate", async (Guid teacherId, SetRateCommand command, PayrollHandlers handlers, CancellationToken ct) =>
            ApiResults.From(await handlers.SetRateAsync(teacherId, command, ct), Results.Ok));
        payroll.MapGet("/rates", async (PayrollHandlers handlers, CancellationToken ct) => Results.Ok(await handlers.ListRatesAsync(ct)));
        payroll.MapGet("/timesheets", async (DateOnly from, DateOnly to, Guid? teacherId, PayrollHandlers handlers, CancellationToken ct) =>
            Results.Ok(await handlers.ListAsync(teacherId, from, to, ct)));
        payroll.MapGet("/payroll-periods", async (PayrollHandlers handlers, CancellationToken ct) =>
            Results.Ok(await handlers.ListPeriodsAsync(ct)));
        payroll.MapPost("/payroll-periods", async (CreatePeriodCommand command, PayrollHandlers handlers, CancellationToken ct) =>
            ApiResults.From(await handlers.CreatePeriodAsync(command, ct), p => Results.Created($"payroll-periods/{p.Id}", p)));
        payroll.MapPost("/payroll-periods/{id:guid}/lock", async (Guid id, PayrollHandlers handlers, CancellationToken ct) =>
            ApiResults.From(await handlers.LockAsync(id, ct), Results.Ok));

        // Teacher app: own timesheets only.
        group.MapGet("/my/timesheets", async (DateOnly from, DateOnly to, ClaimsPrincipal user, PayrollHandlers handlers, CancellationToken ct) =>
                Results.Ok(await handlers.ListAsync(user.UserId()!.Value, from, to, ct)))
            .RequireAuthorization(Policies.Staff);
    }
}
