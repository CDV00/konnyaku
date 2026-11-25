using System.Security.Claims;
using Konnyaku.Academic.Contracts;
using Konnyaku.Application.Messaging;
using Konnyaku.Billing.Application;
using Konnyaku.Billing.Domain;
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
using Microsoft.EntityFrameworkCore;
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
        services.AddScoped<RefundHandlers>();
        services.AddScoped<ReceiptService>();
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

        // Packages are center-wide: a branch accountant reads them but does not change prices.
        var packages = group.MapGroup("").RequireCenterPermission(Permissions.Packages);
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
        sales.MapGet("/orders", async (Guid? studentId, string? status, BillingQueries queries, CancellationToken ct) =>
            ApiResults.From(await queries.ListOrdersAsync(studentId, status, ct), Results.Ok));
        payments.MapPost("/orders/{id:guid}/payments", async (Guid id, RecordPaymentCommand command, HttpContext http, OrderHandlers handlers, CancellationToken ct) =>
            ApiResults.From(await handlers.RecordPaymentAsync(id, command, Actor(http), ct), Results.Ok));
        orders.MapPost("/accounts/{orderId:guid}/adjustments", async (Guid orderId, AdjustCreditsCommand command, AdjustCreditsHandler handler, CancellationToken ct) =>
            ApiResults.From(await handler.HandleAsync(orderId, command, ct), Results.Ok));
        sales.MapGet("/accounts/{orderId:guid}/ledger", async (Guid orderId, BillingQueries queries, CancellationToken ct) =>
            Results.Ok(await queries.ListLedgerAsync(orderId, ct)));

        var refunds = group.MapGroup("").RequirePermission(Permissions.Refunds);
        refunds.MapGet("/orders/{id:guid}/refund-quote", async (Guid id, RefundHandlers handlers, CancellationToken ct) =>
            ApiResults.From(await handlers.QuoteAsync(id, ct), Results.Ok));
        refunds.MapPost("/orders/{id:guid}/refunds", async (Guid id, RefundCommand command, HttpContext http, RefundHandlers handlers, CancellationToken ct) =>
            ApiResults.From(await handlers.RefundAsync(id, command, Actor(http), ct), r => Results.Created($"refunds/{r.Id}", r)));
        sales.MapGet("/orders/{id:guid}/refunds", async (Guid id, IBillingDbContext db, CancellationToken ct) =>
            Results.Ok((await db.Refunds.AsNoTracking().Where(r => r.OrderId == id).ToListAsync(ct)).Select(r => r.ToView())));

        // Receipts (phiếu thu) and refund vouchers (phiếu chi): kind is "receipt" or "refund".
        var receipts = group.MapGroup("").RequirePermission(Permissions.Payments, Permissions.Refunds);
        receipts.MapGet("/receipts/{kind}/{id:guid}", async (string kind, Guid id, ReceiptService service, CancellationToken ct) =>
            ApiResults.From(await service.GetAsync(kind, id, ct), Results.Ok));
        receipts.MapGet("/receipts/{kind}/{id:guid}/pdf", async (string kind, Guid id, bool? download, string? locale, HttpContext http,
                ReceiptService service, CancellationToken ct) =>
            ApiResults.From(await service.PrintAsync(kind, id, download ?? false, locale, Actor(http), null, ct), f => Pdf(http, f, download ?? false)));
        receipts.MapGet("/receipts/{kind}/{id:guid}/prints", async (string kind, Guid id, ReceiptService service, CancellationToken ct) =>
            Results.Ok(await service.ListPrintsAsync(id, ct)));
        sales.MapGet("/students/{studentId:guid}/credits", async (Guid studentId, BillingQueries queries, CancellationToken ct) =>
            Results.Ok(await queries.ListCreditAccountsAsync([studentId], ct)));
        finance.MapGet("/reports/recognized-revenue", async (DateOnly from, DateOnly to, BillingQueries queries, CancellationToken ct) =>
            Results.Ok(await queries.RecognizedRevenueAsync(from, to, ct)));

        group.MapGet("/my/credits", async (ClaimsPrincipal user, BillingQueries queries, CancellationToken ct) =>
                Results.Ok(await queries.ListLearnerAccountsAsync(user.UserId()!.Value, ct)))
            .RequireAuthorization(Policies.Learn);
        group.MapGet("/my/payments", async (ClaimsPrincipal user, ReceiptService service, CancellationToken ct) =>
                Results.Ok(await service.ListMineAsync(user.UserId()!.Value, ct)))
            .RequireAuthorization(Policies.Learn);
        group.MapGet("/my/receipts/{kind}/{id:guid}/pdf", async (string kind, Guid id, bool? download, string? locale, HttpContext http,
                    ReceiptService service, CancellationToken ct) =>
                ApiResults.From(await service.PrintAsync(kind, id, download ?? false, locale, Actor(http), http.User.UserId()!.Value, ct),
                    f => Pdf(http, f, download ?? false)))
            .RequireAuthorization(Policies.Learn);
    }

    private static RequestActor Actor(HttpContext http) =>
        new(http.User.UserId(), http.User.FindFirstValue(KonnyakuClaims.Name), http.Connection.RemoteIpAddress?.ToString());

    /// <summary>Inline (opened in the browser to print) or as a download; never cached by the browser or a proxy.</summary>
    private static IResult Pdf(HttpContext http, ReceiptFile file, bool download)
    {
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.ContentDisposition = $"{(download ? "attachment" : "inline")}; filename=\"{file.FileName}\"";
        return Results.Bytes(file.Content, "application/pdf");
    }
}
