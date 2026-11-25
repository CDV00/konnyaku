using Konnyaku.SharedKernel;

namespace Konnyaku.Billing.Domain;

public enum RefundKind
{
    /// <summary>A paid order the class could not take (full or closed).</summary>
    ClassUnavailable,

    /// <summary>A student leaving a paid course: unused paid lessons are refunded.</summary>
    Withdrawal,

    /// <summary>A student stopping before paying in full: what was received is refunded.</summary>
    Cancellation,
}

/// <summary>
/// The refund rule, in one place. Unused paid lessons are worth <c>remaining paid credits × unit amount</c> (the whole
/// order amount when none was used, so rounding never keeps money); bonus lessons are worth nothing; credits added later
/// by an adjustment are not refunded (capped at the credits bought). An order without credits yet (not fully paid)
/// refunds what was received. The suggestion never exceeds what was received.
/// </summary>
public sealed record RefundQuote(
    RefundKind Kind, decimal AmountReceived, decimal PaidCreditsLeft, decimal BonusCreditsLeft, decimal UnitAmount, decimal UnusedValue,
    decimal SuggestedAmount)
{
    public static RefundQuote For(Order order, RefundKind kind, CreditBalance? account)
    {
        if (account is null)
        {
            return new RefundQuote(kind, order.AmountReceived, 0, 0, order.UnitAmount, order.AmountReceived, order.AmountReceived);
        }

        var paidLeft = Math.Clamp(account.PaidCredits, 0, order.PaidCredits);
        var unused = paidLeft >= order.PaidCredits ? order.Amount : decimal.Round(paidLeft * account.UnitAmount, 2);
        return new RefundQuote(kind, order.AmountReceived, Math.Max(account.PaidCredits, 0), Math.Max(account.BonusCredits, 0),
            account.UnitAmount, unused, Math.Min(unused, order.AmountReceived));
    }
}

/// <summary>
/// Money paid back for one order (a phiếu chi with its own consecutive number). Never edited. The difference between the
/// value of the unused lessons and the amount paid back (a fee kept, or a goodwill refund above it) is recognized as
/// revenue on <see cref="LocalDate"/>, for the branch that collected the order.
/// </summary>
public sealed class Refund : Entity, ITenantOwned, IAuditable
{
    private Refund()
    {
    }

    public Refund(Order order, RefundKind kind, long receiptNo, RefundQuote quote, decimal amount, string method, string? reference,
        string reason, decimal paidCreditsReturned, decimal bonusCreditsForfeited, Guid? approvedById, string? approvedByName,
        DateTimeOffset now, DateOnly localDate)
    {
        OrderId = order.Id;
        StudentId = order.StudentId;
        BranchId = order.BranchId;
        Kind = kind;
        ReceiptNo = receiptNo;
        Amount = amount;
        SuggestedAmount = quote.SuggestedAmount;
        UnusedValue = quote.UnusedValue;
        RetainedAmount = quote.UnusedValue - amount;
        Currency = order.Currency;
        Method = method;
        Reference = string.IsNullOrWhiteSpace(reference) ? null : reference.Trim();
        Reason = reason;
        PaidCreditsReturned = paidCreditsReturned;
        BonusCreditsForfeited = bonusCreditsForfeited;
        ApprovedById = approvedById;
        ApprovedByName = approvedByName;
        RefundedAt = now;
        LocalDate = localDate;
    }

    public Guid TenantId { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid StudentId { get; private set; }

    /// <summary>Branch that collected the order (its revenue absorbs <see cref="RetainedAmount"/>).</summary>
    public Guid? BranchId { get; private set; }

    public RefundKind Kind { get; private set; }

    /// <summary>Number of its voucher (phiếu chi), consecutive per center.</summary>
    public long ReceiptNo { get; private set; }

    public decimal Amount { get; private set; }

    /// <summary>What the rule suggested (<see cref="RefundQuote.SuggestedAmount"/>); differs from <see cref="Amount"/> when overridden.</summary>
    public decimal SuggestedAmount { get; private set; }

    public decimal UnusedValue { get; private set; }

    /// <summary><see cref="UnusedValue"/> − <see cref="Amount"/>: revenue recognized by the refund (negative when refunding more).</summary>
    public decimal RetainedAmount { get; private set; }

    public string Currency { get; private set; } = string.Empty;
    public string Method { get; private set; } = string.Empty;
    public string? Reference { get; private set; }
    public string Reason { get; private set; } = string.Empty;
    public decimal PaidCreditsReturned { get; private set; }
    public decimal BonusCreditsForfeited { get; private set; }

    /// <summary>The staff member who approved and recorded the refund.</summary>
    public Guid? ApprovedById { get; private set; }

    public string? ApprovedByName { get; private set; }
    public DateTimeOffset RefundedAt { get; private set; }

    /// <summary>Day of the refund in the branch's time zone: the revenue period of <see cref="RetainedAmount"/>.</summary>
    public DateOnly LocalDate { get; private set; }

    /// <summary>Logged under its order.</summary>
    Guid IAuditable.AuditSubjectId => OrderId;
}

/// <summary>Kinds of numbered documents: each has its own consecutive counter per center.</summary>
public static class DocumentKinds
{
    /// <summary>Phiếu thu — one per payment received.</summary>
    public const string Receipt = "receipt";

    /// <summary>Phiếu chi — one per refund paid.</summary>
    public const string RefundVoucher = "refund";

    public static bool IsKnown(string kind) => kind is Receipt or RefundVoucher;
}

/// <summary>
/// Last number used for a kind of document of a center. Incremented with a single row-locking upsert inside the
/// transaction that saves the document, so concurrent saves wait for each other and a rolled-back save gives its number
/// back: numbers never repeat and never skip.
/// </summary>
public sealed class DocumentSequence : ITenantOwned
{
    private DocumentSequence()
    {
    }

    public Guid TenantId { get; private set; }
    public string Kind { get; private set; } = string.Empty;
    public long LastValue { get; private set; }
}

/// <summary>Who printed or downloaded which receipt, when and from where. Append-only; holds no personal data of the student.</summary>
public sealed class ReceiptPrintLog : Entity, ITenantOwned
{
    public const string Print = "print";
    public const string Download = "download";

    private ReceiptPrintLog()
    {
    }

    public ReceiptPrintLog(string documentKind, Guid documentId, long receiptNo, string action, string audience, Guid? actorId,
        string? actorName, string? clientIp, DateTimeOffset now)
    {
        DocumentKind = documentKind;
        DocumentId = documentId;
        ReceiptNo = receiptNo;
        Action = action;
        Audience = audience;
        ActorId = actorId;
        ActorName = actorName;
        ClientIp = clientIp;
        OccurredAt = now;
    }

    public Guid TenantId { get; private set; }

    /// <summary><see cref="DocumentKinds.Receipt"/> (a payment) or <see cref="DocumentKinds.RefundVoucher"/> (a refund).</summary>
    public string DocumentKind { get; private set; } = string.Empty;

    public Guid DocumentId { get; private set; }
    public long ReceiptNo { get; private set; }

    /// <summary><see cref="Print"/> (opened in the browser to print) or <see cref="Download"/>.</summary>
    public string Action { get; private set; } = string.Empty;

    /// <summary><c>staff</c> or <c>learn</c> (the student or parent).</summary>
    public string Audience { get; private set; } = string.Empty;

    public Guid? ActorId { get; private set; }
    public string? ActorName { get; private set; }
    public string? ClientIp { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
}
