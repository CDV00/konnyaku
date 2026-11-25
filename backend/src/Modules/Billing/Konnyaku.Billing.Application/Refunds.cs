using Konnyaku.Application.Authorization;
using Konnyaku.Application.Persistence;
using Konnyaku.Application.Tenancy;
using Konnyaku.Billing.Contracts;
using Konnyaku.Billing.Domain;
using Konnyaku.Organization.Contracts;
using Konnyaku.Platform.Contracts;
using Konnyaku.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Konnyaku.Billing.Application;

/// <summary>
/// What a refund of the order would pay back under the rule (see <see cref="RefundQuote"/>). <see cref="Refundable"/> is
/// false (with <see cref="Kind"/> null) when the order is not in a refundable state.
/// </summary>
public sealed record RefundQuoteView(
    Guid OrderId, bool Refundable, string? Kind, string Status, decimal AmountReceived, decimal PaidCreditsLeft, decimal BonusCreditsLeft,
    decimal UnitAmount, decimal UnusedValue, decimal SuggestedAmount, string Currency);

/// <summary>
/// <see cref="Amount"/> null takes the suggested amount; another amount (never above what was received) overrides it. The
/// reason is always required — it is printed on the voucher and kept in the ledger.
/// </summary>
public sealed record RefundCommand(decimal? Amount, string? Method, string? Reference, string? Reason);

public sealed record RefundView(
    Guid Id, Guid OrderId, string Kind, long ReceiptNo, string ReceiptCode, decimal Amount, decimal SuggestedAmount, decimal UnusedValue,
    decimal RetainedAmount, string Currency, string Method, string? Reference, string Reason, decimal PaidCreditsReturned,
    decimal BonusCreditsForfeited, string? ApprovedByName, DateTimeOffset RefundedAt, DateOnly LocalDate);

/// <summary>
/// Refunds: one per order, approved and recorded by an accountant of the order's branch. Credits leave the ledger with
/// reversing lines (never by editing a line), the order becomes <see cref="OrderStatus.Refunded"/>, a numbered voucher
/// (phiếu chi) is issued and <see cref="OrderRefundedV1"/> tells Academic to free the seat.
/// </summary>
public sealed class RefundHandlers(
    IBillingDbContext db, IAccessControl access, IOrganizationApi organization, IPlatformApi platform, ITenantContext tenant,
    IDbErrorClassifier errors, TimeProvider clock)
{
    public const int ReasonMaxLength = 300;

    public async Task<Result<RefundQuoteView>> QuoteAsync(Guid orderId, CancellationToken ct)
    {
        var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null)
        {
            return Error.NotFound("billing.order_not_found");
        }

        if (await access.EnsureAsync(Permissions.Refunds, ct, order.BranchId) is { } forbidden)
        {
            return forbidden;
        }

        if (order.RefundKind is not { } kind)
        {
            return new RefundQuoteView(order.Id, false, null, order.Status.ToString(), order.AmountReceived, 0, 0, order.UnitAmount, 0, 0, order.Currency);
        }

        var account = await db.Balances.AsNoTracking().FirstOrDefaultAsync(b => b.OrderId == orderId, ct);
        return ToView(order, RefundQuote.For(order, kind, account));
    }

    public async Task<Result<RefundView>> RefundAsync(Guid orderId, RefundCommand command, RequestActor actor, CancellationToken ct)
    {
        var reason = command.Reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > ReasonMaxLength)
        {
            return Error.Validation("billing.refund_reason_required", new Dictionary<string, object?> { ["max"] = ReasonMaxLength });
        }

        // The voucher number first: it serializes the center's refunds, so the order and balance read next are current.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var receiptNo = await db.NextDocumentNumberAsync(DocumentKinds.RefundVoucher, ct);
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null)
        {
            return Error.NotFound("billing.order_not_found");
        }

        if (await access.EnsureAsync(Permissions.Refunds, ct, order.BranchId) is { } forbidden)
        {
            return forbidden;
        }

        var account = await db.Balances.FirstOrDefaultAsync(b => b.OrderId == orderId, ct);
        var quote = order.RefundKind is { } refundKind ? RefundQuote.For(order, refundKind, account) : null;
        var amount = command.Amount ?? quote?.SuggestedAmount ?? 0;
        var now = clock.GetUtcNow();
        var refunded = order.MarkRefunded(amount, now);
        if (!refunded.IsSuccess)
        {
            return refunded.Error!;
        }

        var lines = account?.CloseForRefund(order.BranchId, reason, now) ?? [];
        db.Ledger.AddRange(lines);
        var paidReturned = -lines.Where(l => l.CreditKind == CreditKind.Paid).Sum(l => l.Credits);
        var bonusForfeited = -lines.Where(l => l.CreditKind == CreditKind.Bonus).Sum(l => l.Credits);
        var refund = new Refund(order, refunded.Value, receiptNo, quote!, amount,
            string.IsNullOrWhiteSpace(command.Method) ? "cash" : command.Method.Trim(), command.Reference, reason, paidReturned, bonusForfeited,
            actor.UserId, actor.Name, now, await LocalDateAsync(order.BranchId, now, ct));
        db.Refunds.Add(refund);
        if (account is not null && lines.Count > 0)
        {
            db.Publish(new CreditBalanceChangedV1(account.OrderId, account.StudentId, account.EnrollmentId, account.PaidCredits, account.BonusCredits));
        }

        db.Publish(new OrderRefundedV1(order.Id, order.StudentId, order.ClassId, account?.EnrollmentId, amount, order.Currency));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (errors.IsUniqueViolation(ex) || ex is DbUpdateConcurrencyException)
        {
            return Error.Conflict("billing.order_already_refunded");
        }

        await transaction.CommitAsync(ct);
        return refund.ToView();
    }

    /// <summary>The day in the collecting branch's time zone (the center's when the order has no branch).</summary>
    private async Task<DateOnly> LocalDateAsync(Guid? branchId, DateTimeOffset now, CancellationToken ct)
    {
        var zone = branchId is { } id ? (await organization.GetBranchAsync(id, ct))?.TimeZone : null;
        zone ??= tenant.TenantId is { } tenantId ? (await platform.GetTenantAsync(tenantId, ct))?.TimeZone : null;
        return DateOnly.FromDateTime(TimeZones.ToLocal(now, zone).DateTime);
    }

    private static RefundQuoteView ToView(Order order, RefundQuote quote) => new(
        order.Id, true, quote.Kind.ToString(), order.Status.ToString(), quote.AmountReceived, quote.PaidCreditsLeft, quote.BonusCreditsLeft,
        quote.UnitAmount, quote.UnusedValue, quote.SuggestedAmount, order.Currency);
}

public static class TimeZones
{
    /// <summary>The instant in an IANA zone; an unknown or missing zone falls back to UTC.</summary>
    public static DateTimeOffset ToLocal(DateTimeOffset instant, string? zone)
    {
        if (!string.IsNullOrWhiteSpace(zone) && TimeZoneInfo.TryFindSystemTimeZoneById(zone, out var info))
        {
            return TimeZoneInfo.ConvertTime(instant, info);
        }

        return instant.ToUniversalTime();
    }
}

public static class RefundMapping
{
    public static RefundView ToView(this Refund r) => new(
        r.Id, r.OrderId, r.Kind.ToString(), r.ReceiptNo, ReceiptCodes.For(DocumentKinds.RefundVoucher, r.ReceiptNo), r.Amount, r.SuggestedAmount,
        r.UnusedValue, r.RetainedAmount, r.Currency, r.Method, r.Reference, r.Reason, r.PaidCreditsReturned, r.BonusCreditsForfeited,
        r.ApprovedByName, r.RefundedAt, r.LocalDate);
}
