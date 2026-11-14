using System.Security.Claims;
using Konnyaku.Academic.Contracts;
using Konnyaku.Application.Messaging;
using Konnyaku.Billing.Application;
using Konnyaku.Billing.Infrastructure;
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
        services.AddScoped<ConsumeCreditHandler>();
        services.AddScoped<AdjustCreditsHandler>();
        services.AddScoped<IIntegrationEventHandler<EnrollmentActivatedV1>, EnrollmentActivatedConsumer>();
        services.AddScoped<IIntegrationEventHandler<EnrollmentRejectedV1>, EnrollmentRejectedConsumer>();
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

        finance.MapPost("/packages", async (CreatePackageCommand command, OrderHandlers handlers, CancellationToken ct) =>
            ApiResults.From(await handlers.CreatePackageAsync(command, ct), p => Results.Created($"packages/{p.Id}", p)));
        sales.MapPost("/orders", async (CreateOrderCommand command, OrderHandlers handlers, CancellationToken ct) =>
            ApiResults.From(await handlers.CreateOrderAsync(command, ct), o => Results.Created($"orders/{o.Id}", o)));
        sales.MapGet("/orders", async (Guid? studentId, BillingQueries queries, CancellationToken ct) =>
            Results.Ok(await queries.ListOrdersAsync(studentId, ct)));
        finance.MapPost("/orders/{id:guid}/payments", async (Guid id, RecordPaymentCommand command, OrderHandlers handlers, CancellationToken ct) =>
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
