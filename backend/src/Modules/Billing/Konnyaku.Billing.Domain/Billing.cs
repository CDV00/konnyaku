using Konnyaku.SharedKernel;

namespace Konnyaku.Billing.Domain;

/// <summary>A sellable package: N paid credits (lessons) + optional bonus credits for a price.</summary>
public sealed class Package : Entity, ITenantOwned, IAuditable
{
    private Package()
    {
    }

    public Guid TenantId { get; private set; }
    public string Code { get; private set; } = string.Empty;
    public LocalizedString Name { get; private set; } = null!;
    public decimal PaidCredits { get; private set; }
    public decimal BonusCredits { get; private set; }
    public decimal Price { get; private set; }
    public string Currency { get; private set; } = "VND";
    public bool IsActive { get; private set; }

    Guid IAuditable.AuditSubjectId => Id;

    /// <summary>Orders copy amounts and credits when they are placed, so a new price applies to new orders only.</summary>
    public Result<Package> Update(string code, IReadOnlyDictionary<string, string> name, decimal paidCredits, decimal bonusCredits,
        decimal price, string currency)
    {
        var valid = Create(code, name, paidCredits, bonusCredits, price, currency);
        if (!valid.IsSuccess)
        {
            return valid.Error!;
        }

        Code = valid.Value.Code;
        Name = valid.Value.Name;
        PaidCredits = paidCredits;
        BonusCredits = bonusCredits;
        Price = price;
        Currency = valid.Value.Currency;
        return this;
    }

    /// <summary>An inactive package is no longer sold; existing orders keep it.</summary>
    public void SetActive(bool active) => IsActive = active;

    public static Result<Package> Create(string code, IReadOnlyDictionary<string, string> name, decimal paidCredits,
        decimal bonusCredits, decimal price, string currency)
    {
        if (string.IsNullOrWhiteSpace(code) || name is null || name.Values.All(string.IsNullOrWhiteSpace))
        {
            return Error.Validation("billing.package_invalid");
        }

        if (paidCredits <= 0 || bonusCredits < 0 || price < 0 || currency is not { Length: 3 })
        {
            return Error.Validation("billing.package_amounts_invalid");
        }

        return new Package
        {
            Code = code.Trim().ToUpperInvariant(),
            Name = new LocalizedString(name),
            PaidCredits = paidCredits,
            BonusCredits = bonusCredits,
            Price = price,
            Currency = currency.ToUpperInvariant(),
            IsActive = true,
        };
    }
}

public enum OrderStatus
{
    PendingPayment,
    Paid,
    Cancelled,
    RefundRequired,

    /// <summary>Money was paid back (see <see cref="Refund"/>); the order is closed.</summary>
    Refunded,
}

/// <summary>A student buying a package (optionally for a class). Amounts and credits are snapshots of the package.</summary>
public sealed class Order : Entity, ITenantOwned, IAuditable
{
    private Order()
    {
    }

    public Order(Guid studentId, Package package, Guid? classId, Guid? branchId, DateTimeOffset now)
    {
        StudentId = studentId;
        PackageId = package.Id;
        ClassId = classId;
        BranchId = branchId;
        Amount = package.Price;
        Currency = package.Currency;
        PaidCredits = package.PaidCredits;
        BonusCredits = package.BonusCredits;
        Status = OrderStatus.PendingPayment;
        CreatedAt = now;
    }

    public Guid TenantId { get; private set; }
    public Guid StudentId { get; private set; }
    public Guid PackageId { get; private set; }
    public Guid? ClassId { get; private set; }

    /// <summary>Branch that collected the money (revenue may be recognized elsewhere — see the ledger).</summary>
    public Guid? BranchId { get; private set; }

    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = string.Empty;
    public decimal PaidCredits { get; private set; }
    public decimal BonusCredits { get; private set; }
    public decimal AmountReceived { get; private set; }
    public OrderStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? PaidAt { get; private set; }
    public decimal AmountRefunded { get; private set; }
    public DateTimeOffset? RefundedAt { get; private set; }

    /// <summary>Value of one paid credit, used for revenue recognition and refunds. Bonus credits are worth 0.</summary>
    public decimal UnitAmount => PaidCredits == 0 ? 0 : decimal.Round(Amount / PaidCredits, 2);

    public Result<bool> ReceivePayment(decimal amount, DateTimeOffset now)
    {
        if (Status != OrderStatus.PendingPayment)
        {
            return Error.Conflict("billing.order_not_payable");
        }

        if (amount <= 0 || AmountReceived + amount > Amount)
        {
            return Error.Validation("billing.payment_amount_invalid",
                new Dictionary<string, object?> { ["outstanding"] = Amount - AmountReceived });
        }

        AmountReceived += amount;
        if (AmountReceived < Amount)
        {
            return false;
        }

        Status = OrderStatus.Paid;
        PaidAt = now;
        return true;
    }

    public void RequireRefund() => Status = OrderStatus.RefundRequired;

    /// <summary>
    /// Why money can be paid back: the class could not take the student (<see cref="RefundKind.ClassUnavailable"/>), the
    /// student leaves a paid course (<see cref="RefundKind.Withdrawal"/>) or stops before paying in full
    /// (<see cref="RefundKind.Cancellation"/>). Null when nothing can be refunded.
    /// </summary>
    public RefundKind? RefundKind => Status switch
    {
        OrderStatus.RefundRequired => Domain.RefundKind.ClassUnavailable,
        OrderStatus.Paid => Domain.RefundKind.Withdrawal,
        OrderStatus.PendingPayment when AmountReceived > 0 => Domain.RefundKind.Cancellation,
        _ => null,
    };

    /// <summary>One refund per order: it closes the order. The amount can never exceed what was received.</summary>
    public Result<RefundKind> MarkRefunded(decimal amount, DateTimeOffset now)
    {
        if (Status == OrderStatus.Refunded)
        {
            return Error.Conflict("billing.order_already_refunded");
        }

        if (RefundKind is not { } kind)
        {
            return Error.Conflict("billing.order_not_refundable");
        }

        if (amount <= 0 || amount > AmountReceived)
        {
            return Error.Validation("billing.refund_amount_invalid", new Dictionary<string, object?> { ["max"] = AmountReceived });
        }

        AmountRefunded = amount;
        RefundedAt = now;
        Status = OrderStatus.Refunded;
        return kind;
    }

    Guid IAuditable.AuditSubjectId => Id;

    /// <summary>
    /// Price (a discount, for example) and target class of an order still waiting for payment. Once paid, its credits are
    /// in the ledger: corrections go through ledger adjustments instead.
    /// </summary>
    public Result<Order> Edit(decimal amount, Guid? classId, Guid? branchId)
    {
        if (Status != OrderStatus.PendingPayment)
        {
            return Error.Conflict("billing.order_not_editable");
        }

        if (amount < 0 || amount <= AmountReceived)
        {
            return Error.Validation("billing.order_amount_invalid", new Dictionary<string, object?> { ["received"] = AmountReceived });
        }

        Amount = amount;
        ClassId = classId;
        BranchId = branchId;
        return this;
    }

    /// <summary>An order nobody paid anything for can be cancelled; one with payments needs its payments voided first.</summary>
    public Result<Order> Cancel()
    {
        if (Status != OrderStatus.PendingPayment)
        {
            return Error.Conflict("billing.order_not_editable");
        }

        if (AmountReceived > 0)
        {
            return Error.Conflict("billing.order_has_payments");
        }

        Status = OrderStatus.Cancelled;
        return this;
    }

    /// <summary>A payment of a pending order was corrected or voided (the order stays below its amount).</summary>
    public Result<Order> CorrectReceived(decimal delta)
    {
        if (Status != OrderStatus.PendingPayment)
        {
            return Error.Conflict("billing.order_not_editable");
        }

        var received = AmountReceived + delta;
        if (received < 0 || received >= Amount)
        {
            return Error.Validation("billing.payment_amount_invalid", new Dictionary<string, object?> { ["outstanding"] = Amount - AmountReceived });
        }

        AmountReceived = received;
        return this;
    }
}

public sealed class Payment : Entity, ITenantOwned, IAuditable
{
    private Payment()
    {
    }

    public Payment(Guid orderId, long receiptNo, decimal amount, string method, string? reference, DateTimeOffset receivedAt,
        Guid? receivedById, string? receivedByName)
    {
        OrderId = orderId;
        ReceiptNo = receiptNo;
        Amount = amount;
        Method = method;
        Reference = string.IsNullOrWhiteSpace(reference) ? null : reference.Trim();
        ReceivedAt = receivedAt;
        ReceivedById = receivedById;
        ReceivedByName = receivedByName;
    }

    public Guid TenantId { get; private set; }
    public Guid OrderId { get; private set; }

    /// <summary>Number of its receipt (phiếu thu), consecutive per center — see <see cref="DocumentKinds"/>.</summary>
    public long ReceiptNo { get; private set; }

    public decimal Amount { get; private set; }
    public string Method { get; private set; } = string.Empty;
    public string? Reference { get; private set; }
    public DateTimeOffset ReceivedAt { get; private set; }

    /// <summary>The staff member who recorded the money (printed on the receipt as the collector).</summary>
    public Guid? ReceivedById { get; private set; }

    public string? ReceivedByName { get; private set; }

    /// <summary>Set when the payment was entered by mistake; the row stays as a trace.</summary>
    public DateTimeOffset? VoidedAt { get; private set; }

    /// <summary>Logged under its order.</summary>
    Guid IAuditable.AuditSubjectId => OrderId;

    public void Correct(decimal amount, string method, string? reference)
    {
        Amount = amount;
        Method = method;
        Reference = string.IsNullOrWhiteSpace(reference) ? null : reference.Trim();
    }

    public void Void(DateTimeOffset now) => VoidedAt = now;
}

public enum LedgerEntryType
{
    Purchase,
    Bonus,
    Consume,
    Refund,
    TransferOut,
    TransferIn,
    Expire,
    Adjust,
}

public enum CreditKind
{
    Paid,
    Bonus,
}

/// <summary>Append-only line of the lesson ledger. Never updated or deleted — mistakes are corrected by an Adjust line.</summary>
public sealed class CreditLedgerEntry : Entity, ITenantOwned
{
    private CreditLedgerEntry()
    {
    }

    public Guid TenantId { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid StudentId { get; private set; }
    public Guid? EnrollmentId { get; private set; }

    /// <summary>Purchase: branch that collected. Consume: branch of the class that taught the lesson.</summary>
    public Guid? BranchId { get; private set; }

    public LedgerEntryType EntryType { get; private set; }
    public CreditKind CreditKind { get; private set; }
    public decimal Credits { get; private set; }
    public decimal UnitAmount { get; private set; }
    public string Currency { get; private set; } = string.Empty;
    public Guid? AttendanceId { get; private set; }
    public DateOnly? SessionLocalDate { get; private set; }
    public string? Reason { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    internal static CreditLedgerEntry Line(CreditBalance account, LedgerEntryType type, CreditKind kind, decimal credits,
        decimal unitAmount, Guid? branchId, DateTimeOffset now, Guid? attendanceId = null, DateOnly? sessionDate = null, string? reason = null) => new()
    {
        OrderId = account.OrderId,
        StudentId = account.StudentId,
        EnrollmentId = account.EnrollmentId,
        BranchId = branchId,
        EntryType = type,
        CreditKind = kind,
        Credits = credits,
        UnitAmount = unitAmount,
        Currency = account.Currency,
        AttendanceId = attendanceId,
        SessionLocalDate = sessionDate,
        Reason = reason,
        CreatedAt = now,
    };
}

/// <summary>
/// Running balance of one purchase (one order), kept in the same transaction as its ledger lines. It becomes linked to
/// the enrollment that uses it. Paid credits may go below zero: the center lets students owe lessons.
/// </summary>
public sealed class CreditBalance : ITenantOwned
{
    public const decimal LowThreshold = 3;

    private CreditBalance()
    {
    }

    public Guid TenantId { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid StudentId { get; private set; }
    public Guid? EnrollmentId { get; private set; }
    public decimal PaidCredits { get; private set; }
    public decimal BonusCredits { get; private set; }
    public decimal UnitAmount { get; private set; }
    public string Currency { get; private set; } = string.Empty;
    public uint Version { get; private set; }

    public decimal Total => PaidCredits + BonusCredits;

    public static (CreditBalance Account, IReadOnlyList<CreditLedgerEntry> Lines) Open(Order order, DateTimeOffset now)
    {
        var account = new CreditBalance
        {
            OrderId = order.Id,
            StudentId = order.StudentId,
            UnitAmount = order.UnitAmount,
            Currency = order.Currency,
            PaidCredits = order.PaidCredits,
            BonusCredits = order.BonusCredits,
        };
        var lines = new List<CreditLedgerEntry>
        {
            CreditLedgerEntry.Line(account, LedgerEntryType.Purchase, CreditKind.Paid, order.PaidCredits, account.UnitAmount, order.BranchId, now),
        };
        if (order.BonusCredits > 0)
        {
            lines.Add(CreditLedgerEntry.Line(account, LedgerEntryType.Bonus, CreditKind.Bonus, order.BonusCredits, 0, order.BranchId, now));
        }

        return (account, lines);
    }

    public void LinkEnrollment(Guid enrollmentId) => EnrollmentId = enrollmentId;

    /// <summary>One attended lesson: paid credits first (revenue is recognized), then bonus credits.</summary>
    public CreditLedgerEntry Consume(decimal credits, Guid attendanceId, Guid branchId, DateOnly sessionDate, DateTimeOffset now)
    {
        if (PaidCredits <= 0 && BonusCredits >= credits)
        {
            BonusCredits -= credits;
            return CreditLedgerEntry.Line(this, LedgerEntryType.Consume, CreditKind.Bonus, -credits, 0, branchId, now, attendanceId, sessionDate);
        }

        PaidCredits -= credits;
        return CreditLedgerEntry.Line(this, LedgerEntryType.Consume, CreditKind.Paid, -credits, UnitAmount, branchId, now, attendanceId, sessionDate);
    }

    public CreditLedgerEntry Adjust(decimal credits, CreditKind kind, string reason, DateTimeOffset now)
    {
        if (kind == CreditKind.Paid)
        {
            PaidCredits += credits;
        }
        else
        {
            BonusCredits += credits;
        }

        return CreditLedgerEntry.Line(this, LedgerEntryType.Adjust, kind, credits, kind == CreditKind.Paid ? UnitAmount : 0, null, now, reason: reason);
    }

    /// <summary>
    /// Closes the account of a refunded order with reversing lines: unused paid credits leave at their unit value (they
    /// were never recognized as revenue), unused bonus credits are forfeited (bonus is never refunded). Credits owed
    /// (below zero) stay as they are.
    /// </summary>
    public IReadOnlyList<CreditLedgerEntry> CloseForRefund(Guid? branchId, string reason, DateTimeOffset now)
    {
        var lines = new List<CreditLedgerEntry>();
        if (PaidCredits > 0)
        {
            lines.Add(CreditLedgerEntry.Line(this, LedgerEntryType.Refund, CreditKind.Paid, -PaidCredits, UnitAmount, branchId, now, reason: reason));
            PaidCredits = 0;
        }

        if (BonusCredits > 0)
        {
            lines.Add(CreditLedgerEntry.Line(this, LedgerEntryType.Refund, CreditKind.Bonus, -BonusCredits, 0, branchId, now, reason: reason));
            BonusCredits = 0;
        }

        return lines;
    }
}
