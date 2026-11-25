using Konnyaku.Academic.Contracts;
using Konnyaku.Application.Authorization;
using Konnyaku.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Konnyaku.Billing.Application;

public sealed record PaymentView(
    Guid Id, Guid OrderId, decimal Amount, string Method, string? Reference, DateTimeOffset ReceivedAt, DateTimeOffset? VoidedAt, long ReceiptNo,
    string ReceiptCode, string? ReceivedByName);

public sealed record EditOrderCommand(decimal Amount, Guid? ClassId, Guid? BranchId);

/// <summary>
/// Corrections by accountants. Everything here acts before money turns into lesson credits: once an order is fully paid,
/// the append-only ledger is the record and mistakes are fixed with an adjustment line.
/// </summary>
public sealed class BillingAdminHandlers(IBillingDbContext db, IAcademicApi academic, IAccessControl access, TimeProvider clock)
{
    public async Task<Result<PackageView>> UpdatePackageAsync(Guid packageId, CreatePackageCommand command, CancellationToken ct)
    {
        var package = await db.Packages.FirstOrDefaultAsync(p => p.Id == packageId, ct);
        if (package is null)
        {
            return Error.NotFound("billing.package_not_found");
        }

        var updated = package.Update(command.Code, command.Name, command.PaidCredits, command.BonusCredits, command.Price, command.Currency ?? string.Empty);
        if (!updated.IsSuccess)
        {
            return updated.Error!;
        }

        if (await db.Packages.AnyAsync(p => p.Id != packageId && p.Code == package.Code, ct))
        {
            return Error.Conflict("billing.package_code_taken");
        }

        await db.SaveChangesAsync(ct);
        return package.ToView();
    }

    public async Task<Result<PackageView>> SetPackageActiveAsync(Guid packageId, bool active, CancellationToken ct)
    {
        var package = await db.Packages.FirstOrDefaultAsync(p => p.Id == packageId, ct);
        if (package is null)
        {
            return Error.NotFound("billing.package_not_found");
        }

        package.SetActive(active);
        await db.SaveChangesAsync(ct);
        return package.ToView();
    }

    public async Task<Result<OrderView>> EditOrderAsync(Guid orderId, EditOrderCommand command, CancellationToken ct)
    {
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null)
        {
            return Error.NotFound("billing.order_not_found");
        }

        var branchId = command.BranchId;
        if (command.ClassId is { } classId)
        {
            var cls = await academic.GetClassAsync(classId, ct);
            if (cls is null)
            {
                return Error.Validation("billing.class_unknown");
            }

            branchId ??= cls.BranchId;
        }

        if (await access.EnsureAsync(Permissions.Orders, ct, order.BranchId, branchId) is { } forbidden)
        {
            return forbidden;
        }

        var edited = order.Edit(command.Amount, command.ClassId, branchId);
        if (!edited.IsSuccess)
        {
            return edited.Error!;
        }

        await db.SaveChangesAsync(ct);
        return order.ToView();
    }

    public async Task<Result<OrderView>> CancelOrderAsync(Guid orderId, CancellationToken ct)
    {
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null)
        {
            return Error.NotFound("billing.order_not_found");
        }

        if (await access.EnsureAsync(Permissions.Orders, ct, order.BranchId) is { } forbidden)
        {
            return forbidden;
        }

        var cancelled = order.Cancel();
        if (!cancelled.IsSuccess)
        {
            return cancelled.Error!;
        }

        await db.SaveChangesAsync(ct);
        return order.ToView();
    }

    /// <summary>Method and reference can always be fixed; the amount only while the order is still waiting for payment.</summary>
    public async Task<Result<PaymentView>> CorrectPaymentAsync(Guid paymentId, RecordPaymentCommand command, CancellationToken ct)
    {
        var (payment, order, error) = await LoadPaymentAsync(paymentId, ct);
        if (error is not null)
        {
            return error;
        }

        if (command.Amount <= 0)
        {
            return Error.Validation("billing.payment_amount_invalid", new Dictionary<string, object?> { ["outstanding"] = order!.Amount - order.AmountReceived });
        }

        if (command.Amount != payment!.Amount)
        {
            var corrected = order!.CorrectReceived(command.Amount - payment.Amount);
            if (!corrected.IsSuccess)
            {
                return corrected.Error!;
            }
        }

        payment.Correct(command.Amount, string.IsNullOrWhiteSpace(command.Method) ? payment.Method : command.Method.Trim(), command.Reference);
        await db.SaveChangesAsync(ct);
        return payment.ToView();
    }

    public async Task<Result<PaymentView>> VoidPaymentAsync(Guid paymentId, CancellationToken ct)
    {
        var (payment, order, error) = await LoadPaymentAsync(paymentId, ct);
        if (error is not null)
        {
            return error;
        }

        var corrected = order!.CorrectReceived(-payment!.Amount);
        if (!corrected.IsSuccess)
        {
            return corrected.Error!;
        }

        payment.Void(clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return payment.ToView();
    }

    private async Task<(Domain.Payment? Payment, Domain.Order? Order, Error? Error)> LoadPaymentAsync(Guid paymentId, CancellationToken ct)
    {
        var payment = await db.Payments.FirstOrDefaultAsync(p => p.Id == paymentId, ct);
        if (payment is null)
        {
            return (null, null, Error.NotFound("billing.payment_not_found"));
        }

        if (payment.VoidedAt is not null)
        {
            return (null, null, Error.Conflict("billing.payment_voided"));
        }

        var order = await db.Orders.FirstAsync(o => o.Id == payment.OrderId, ct);
        return await access.EnsureAsync(Permissions.Payments, ct, order.BranchId) is { } forbidden
            ? (null, null, forbidden)
            : (payment, order, null);
    }
}
