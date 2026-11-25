using System.Security.Claims;
using Konnyaku.Academic.Contracts;
using Konnyaku.Application.Messaging;
using Konnyaku.Billing.Application;
using Konnyaku.Billing.Infrastructure;
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

namespace Konnyaku.Billing.Endpoints;

public sealed class BillingModule : IModule
{
    public string Name => "billing";

    public IReadOnlyList<Type> DbContextTypes => [typeof(BillingDbContext)];

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddBillingInfrastructure(configuration, Name);
        services.AddScoped<OrderHandlers>();
        services.AddScoped<BillingAdminHandlers>();
        services.AddScoped<ConsumeCreditHandler>();
        services.AddScoped<AdjustCreditsHandler>();
        services.AddScoped<Konnyaku.Application.Privacy.IPersonalDataSource, BillingPersonalData>();
        services.AddScoped<IIntegrationEventHandler<EnrollmentActivatedV1>, EnrollmentActivatedConsumer>();
        services.AddScoped<IIntegrationEventHandler<EnrollmentRejectedV1>, EnrollmentRejectedConsumer>();
        services.AddScoped<IIntegrationEventHandler<Konnyaku.Learning.Contracts.AttendanceConfirmedV1>, AttendanceConfirmedConsumer>();
    }

    public void AddWorkerServices(IServiceCollection services) => services.AddOutboxProcessor<BillingDbContext>();

    public void MapEndpoints(RouteGroupBuilder group)
    {
        var finance = group.MapGroup("").RequireAuthorization(p => p.RequireAuthenticatedUser()
            .RequireClaim(KonnyakuClaims.Audience, Audiences.Staff)
            .RequireRole("TenantAdmin", "Accountant"));
        var sales = group.MapGroup("").RequireAuthorization(p => p.RequireAuthenticatedUser()
            .RequireClaim(KonnyakuClaims.Audience, Audiences.Staff)
            .RequireRole("TenantAdmin", "Accountant", "Consultant"));

        var packages = group.MapGroup("").RequirePermission(Permissions.Packages);
        packages.MapPost("/packages", async (CreatePackageCommand command, OrderHandlers handlers, CancellationToken ct) =>
            ApiResults.From(await handlers.CreatePackageAsync(command, ct), p => Results.Created($"packages/{p.Id}", p)));
        packages.MapPut("/packages/{id:guid}", async (Guid id, CreatePackageCommand command, BillingAdminHandlers handlers, CancellationToken ct) =>
            ApiResults.From(await handlers.UpdatePackageAsync(id, command, ct), Results.Ok));
        packages.MapPost("/packages/{id:guid}/deactivate", async (Guid id, BillingAdminHandlers handlers, CancellationToken ct) =>
            ApiResults.From(await handlers.SetPackageActiveAsync(id, false, ct), Results.Ok));
        packages.MapPost("/packages/{id:guid}/activate", async (Guid id, BillingAdminHandlers handlers, CancellationToken ct) =>
            ApiResults.From(await handlers.SetPackageActiveAsync(id, true, ct), Results.Ok));
        sales.MapGet("/packages", async (bool? includeInactive, BillingQueries queries, CancellationToken ct) =>
            Results.Ok(await queries.ListPackagesAsync(includeInactive ?? false, ct)));

        var orders = group.MapGroup("").RequirePermission(Permissions.Orders);
        orders.MapPut("/orders/{id:guid}", async (Guid id, EditOrderCommand command, BillingAdminHandlers handlers, CancellationToken ct) =>
            ApiResults.From(await handlers.EditOrderAsync(id, command, ct), Results.Ok));
        orders.MapPost("/orders/{id:guid}/cancel", async (Guid id, BillingAdminHandlers handlers, CancellationToken ct) =>
            ApiResults.From(await handlers.CancelOrderAsync(id, ct), Results.Ok));

        var payments = group.MapGroup("").RequirePermission(Permissions.Payments);
        payments.MapPut("/payments/{id:guid}", async (Guid id, RecordPaymentCommand command, BillingAdminHandlers handlers, CancellationToken ct) =>
            ApiResults.From(await handlers.CorrectPaymentAsync(id, command, ct), Results.Ok));
        payments.MapPost("/payments/{id:guid}/void", async (Guid id, BillingAdminHandlers handlers, CancellationToken ct) =>
            ApiResults.From(await handlers.VoidPaymentAsync(id, ct), Results.Ok));
        sales.MapGet("/orders/{id:guid}/payments", async (Guid id, BillingQueries queries, CancellationToken ct) =>
            Results.Ok(await queries.ListPaymentsAsync(id, ct)));
        sales.MapChangeHistory<BillingDbContext>();
        sales.MapPost("/orders", async (CreateOrderCommand command, OrderHandlers handlers, CancellationToken ct) =>
            ApiResults.From(await handlers.CreateOrderAsync(command, ct), o => Results.Created($"orders/{o.Id}", o)));
        sales.MapGet("/orders", async ([AsParameters] OrderFilter filter, [AsParameters] ListParams list, BillingQueries queries, CancellationToken ct) =>
            Results.Ok(await queries.ListOrdersAsync(filter, list.ToRequest(BillingQueries.OrderSorts), ct)));
        payments.MapPost("/orders/{id:guid}/payments", async (Guid id, RecordPaymentCommand command, OrderHandlers handlers, CancellationToken ct) =>
            ApiResults.From(await handlers.RecordPaymentAsync(id, command, ct), Results.Ok));
        finance.MapPost("/accounts/{orderId:guid}/adjustments", async (Guid orderId, AdjustCreditsCommand command, AdjustCreditsHandler handler, CancellationToken ct) =>
            ApiResults.From(await handler.HandleAsync(orderId, command, ct), Results.Ok));
        sales.MapGet("/students/{studentId:guid}/credits", async (Guid studentId, BillingQueries queries, CancellationToken ct) =>
            Results.Ok(await queries.ListCreditAccountsAsync([studentId], ct)));
        finance.MapGet("/reports/recognized-revenue", async (DateOnly from, DateOnly to, BillingQueries queries, CancellationToken ct) =>
            Results.Ok(await queries.RecognizedRevenueAsync(from, to, ct)));

        group.MapGet("/my/credits", async (ClaimsPrincipal user, BillingQueries queries, CancellationToken ct) =>
                Results.Ok(await queries.ListLearnerAccountsAsync(user.UserId()!.Value, ct)))
            .RequireAuthorization(Policies.Learn);
    }
}
