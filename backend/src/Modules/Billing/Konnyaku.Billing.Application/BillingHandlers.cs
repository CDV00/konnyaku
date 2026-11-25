using Konnyaku.Academic.Contracts;
using Konnyaku.Application.Authorization;
using Konnyaku.Application.Messaging;
using Konnyaku.Application.Persistence;
using Konnyaku.Billing.Contracts;
using Konnyaku.Billing.Domain;
using Konnyaku.Crm.Contracts;
using Konnyaku.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Konnyaku.Billing.Application;

public interface IBillingDbContext
{
    DbSet<Package> Packages { get; }
    DbSet<Order> Orders { get; }
    DbSet<Payment> Payments { get; }
    DbSet<CreditLedgerEntry> Ledger { get; }
    DbSet<CreditBalance> Balances { get; }
    DbSet<Refund> Refunds { get; }
    DbSet<ReceiptPrintLog> ReceiptPrints { get; }
    DatabaseFacade Database { get; }

    /// <summary>
    /// Next consecutive number of a document kind (<see cref="DocumentKinds"/>) for the current center. Must run inside the
    /// transaction that saves the document: it locks the counter until commit, and a rollback gives the number back.
    /// </summary>
    Task<long> NextDocumentNumberAsync(string kind, CancellationToken cancellationToken);

    void Publish(IIntegrationEvent integrationEvent, string? correlationId = null);

    Task<bool> TryAcceptAsync(Guid eventId, string consumer, CancellationToken cancellationToken);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

public sealed record CreatePackageCommand(string Code, IReadOnlyDictionary<string, string> Name, decimal PaidCredits, decimal BonusCredits, decimal Price, string Currency);

public sealed record PackageView(Guid Id, string Code, IReadOnlyDictionary<string, string> Name, decimal PaidCredits, decimal BonusCredits, decimal Price, string Currency, bool IsActive);

public sealed record CreateOrderCommand(Guid StudentId, Guid PackageId, Guid? ClassId, Guid? BranchId);

public sealed record OrderView(
    Guid Id, Guid StudentId, Guid PackageId, Guid? ClassId, decimal Amount, decimal AmountReceived, string Currency, string Status, Guid? BranchId,
    decimal AmountRefunded, DateTimeOffset CreatedAt);

/// <summary>
/// Filters of the order list (<c>GET /api/billing/orders</c>): <see cref="Q"/> finds the orders of students whose name,
/// phone or email matches (accents ignored, through Crm); <see cref="Status"/> is <c>unpaid</c> (nothing received),
/// <c>partial</c>, <c>paid</c>, <c>refund_required</c>, <c>refunded</c>, <c>cancelled</c> or empty; dates are the order's
/// creation day (UTC).
/// </summary>
public sealed record OrderFilter(
    Guid? StudentId = null, string? Q = null, string? Status = null, Guid? Branch = null, Guid? Package = null, Guid? Class = null,
    DateOnly? CreatedFrom = null, DateOnly? CreatedTo = null);

public sealed record RecordPaymentCommand(decimal Amount, string Method, string? Reference);

/// <summary>The signed-in person behind a request, stamped on documents (collector, approver) and print logs.</summary>
public sealed record RequestActor(Guid? UserId, string? Name, string? ClientIp);

public sealed class OrderHandlers(IBillingDbContext db, ICrmApi crm, IAcademicApi academic, IAccessControl access, TimeProvider clock)
{
    public async Task<Result<PackageView>> CreatePackageAsync(CreatePackageCommand command, CancellationToken ct)
    {
        var created = Package.Create(command.Code, command.Name, command.PaidCredits, command.BonusCredits, command.Price, command.Currency ?? string.Empty);
        if (!created.IsSuccess)
        {
            return created.Error!;
        }

        if (await db.Packages.AnyAsync(p => p.Code == created.Value.Code, ct))
        {
            return Error.Conflict("billing.package_code_taken");
        }

        db.Packages.Add(created.Value);
        await db.SaveChangesAsync(ct);
        return created.Value.ToView();
    }

    public async Task<Result<OrderView>> CreateOrderAsync(CreateOrderCommand command, CancellationToken ct)
    {
        var student = await crm.GetStudentAsync(command.StudentId, ct);
        if (student is null)
        {
            return Error.Validation("billing.student_unknown");
        }

        if (!student.IsActive)
        {
            return Error.Conflict("billing.student_inactive");
        }

        var package = await db.Packages.FirstOrDefaultAsync(p => p.Id == command.PackageId && p.IsActive, ct);
        if (package is null)
        {
            return Error.Validation("billing.package_unknown");
        }

        Guid? branchId = command.BranchId;
        if (command.ClassId is { } classId)
        {
            var cls = await academic.GetClassAsync(classId, ct);
            if (cls is null)
            {
                return Error.Validation("billing.class_unknown");
            }

            branchId ??= cls.BranchId;
        }

        var order = new Order(command.StudentId, package, command.ClassId, branchId, clock.GetUtcNow());
        db.Orders.Add(order);
        await db.SaveChangesAsync(ct);
        return order.ToView();
    }

    /// <summary>
    /// Records money received with its numbered receipt. When the order becomes fully paid, its credits are opened in the
    /// ledger. The receipt number is taken first: it serializes the center's payments, so the order read next is current.
    /// </summary>
    public async Task<Result<OrderView>> RecordPaymentAsync(Guid orderId, RecordPaymentCommand command, RequestActor actor, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var receiptNo = await db.NextDocumentNumberAsync(DocumentKinds.Receipt, ct);
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null)
        {
            return Error.NotFound("billing.order_not_found");
        }

        if (await access.EnsureAsync(Permissions.Payments, ct, order.BranchId) is { } forbidden)
        {
            return forbidden;
        }

        var now = clock.GetUtcNow();
        var paid = order.ReceivePayment(command.Amount, now);
        if (!paid.IsSuccess)
        {
            return paid.Error!;
        }

        db.Payments.Add(new Payment(order.Id, receiptNo, command.Amount, string.IsNullOrWhiteSpace(command.Method) ? "cash" : command.Method.Trim(),
            command.Reference, now, actor.UserId, actor.Name));
        if (paid.Value)
        {
            var (account, lines) = CreditBalance.Open(order, now);
            db.Balances.Add(account);
            db.Ledger.AddRange(lines);
            db.Publish(new OrderPaidV1(order.Id, order.StudentId, order.ClassId, order.PaidCredits, order.BonusCredits));
            db.Publish(new CreditBalanceChangedV1(order.Id, order.StudentId, null, account.PaidCredits, account.BonusCredits));
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return order.ToView();
    }
}

public sealed record ConsumeCreditCommand(Guid EnrollmentId, Guid AttendanceId, Guid BranchId, DateOnly SessionLocalDate, decimal Credits = 1);

/// <summary>Spends one lesson for an attended (billable) session. Idempotent per attendance.</summary>
public sealed class ConsumeCreditHandler(IBillingDbContext db, IDbErrorClassifier errors, TimeProvider clock)
{
    public async Task<Result<ConsumeResult>> HandleAsync(ConsumeCreditCommand command, CancellationToken ct)
    {
        if (await db.Ledger.AnyAsync(l => l.AttendanceId == command.AttendanceId, ct))
        {
            return new ConsumeResult(false, null);
        }

        var account = await db.Balances.FirstOrDefaultAsync(b => b.EnrollmentId == command.EnrollmentId, ct);
        if (account is null)
        {
            // Trial or staff-enrolled without a package: nothing to spend.
            return new ConsumeResult(false, null);
        }

        var line = account.Consume(command.Credits, command.AttendanceId, command.BranchId, command.SessionLocalDate, clock.GetUtcNow());
        db.Ledger.Add(line);
        db.Publish(new CreditBalanceChangedV1(account.OrderId, account.StudentId, account.EnrollmentId, account.PaidCredits, account.BonusCredits));
        if (account.Total <= CreditBalance.LowThreshold)
        {
            db.Publish(new CreditLowV1(account.OrderId, account.StudentId, account.EnrollmentId, account.Total));
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (errors.IsUniqueViolation(ex))
        {
            return new ConsumeResult(false, null);
        }

        return new ConsumeResult(true, account.Total);
    }
}

public sealed record AdjustCreditsCommand(decimal Credits, CreditKind Kind, string Reason);

public sealed class AdjustCreditsHandler(IBillingDbContext db, IAccessControl access, TimeProvider clock)
{
    public async Task<Result<CreditAccountDto>> HandleAsync(Guid orderId, AdjustCreditsCommand command, CancellationToken ct)
    {
        if (command.Credits == 0 || string.IsNullOrWhiteSpace(command.Reason) || command.Reason.Trim().Length > 300)
        {
            return Error.Validation("billing.adjustment_invalid");
        }

        var account = await db.Balances.FirstOrDefaultAsync(b => b.OrderId == orderId, ct);
        if (account is null)
        {
            return Error.NotFound("billing.account_not_found");
        }

        var order = await db.Orders.AsNoTracking().FirstAsync(o => o.Id == orderId, ct);
        if (await access.EnsureAsync(Permissions.Orders, ct, order.BranchId) is { } forbidden)
        {
            return forbidden;
        }

        if (order.Status == OrderStatus.Refunded)
        {
            return Error.Conflict("billing.order_already_refunded");
        }

        db.Ledger.Add(account.Adjust(command.Credits, command.Kind, command.Reason.Trim(), clock.GetUtcNow()));
        db.Publish(new CreditBalanceChangedV1(account.OrderId, account.StudentId, account.EnrollmentId, account.PaidCredits, account.BonusCredits));
        await db.SaveChangesAsync(ct);
        return account.ToDto();
    }
}

/// <summary>Links the purchased credits to the enrollment created from the order.</summary>
public sealed class EnrollmentActivatedConsumer(IBillingDbContext db) : IIntegrationEventHandler<EnrollmentActivatedV1>
{
    public async Task HandleAsync(EnrollmentActivatedV1 @event, IntegrationEventContext context, CancellationToken cancellationToken)
    {
        if (@event.OrderId is not { } orderId || !await db.TryAcceptAsync(context.EventId, nameof(EnrollmentActivatedConsumer), cancellationToken))
        {
            return;
        }

        var account = await db.Balances.FirstOrDefaultAsync(b => b.OrderId == orderId, cancellationToken);
        account?.LinkEnrollment(@event.EnrollmentId);
        await db.SaveChangesAsync(cancellationToken);
    }
}

public sealed class EnrollmentRejectedConsumer(IBillingDbContext db) : IIntegrationEventHandler<EnrollmentRejectedV1>
{
    public async Task HandleAsync(EnrollmentRejectedV1 @event, IntegrationEventContext context, CancellationToken cancellationToken)
    {
        if (!await db.TryAcceptAsync(context.EventId, nameof(EnrollmentRejectedConsumer), cancellationToken))
        {
            return;
        }

        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == @event.OrderId, cancellationToken);
        order?.RequireRefund();
        await db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>Recognized revenue of a branch: <see cref="Amount"/> = <see cref="LessonAmount"/> + <see cref="RefundAdjustment"/>.</summary>
public sealed record RevenueLine(Guid? BranchId, string Currency, decimal Lessons, decimal LessonAmount, decimal RefundAdjustment, decimal Amount);

public sealed class BillingQueries(IBillingDbContext db, ICrmApi crm) : IBillingApi
{
    public async Task<IReadOnlyList<CreditAccountDto>> ListCreditAccountsAsync(IReadOnlyCollection<Guid> studentIds, CancellationToken ct)
    {
        var ids = studentIds.ToArray();
        return (await db.Balances.AsNoTracking().Where(b => ids.Contains(b.StudentId)).ToListAsync(ct)).Select(b => b.ToDto()).ToList();
    }

    public async Task<IReadOnlyList<CreditAccountDto>> ListLearnerAccountsAsync(Guid userId, CancellationToken ct)
    {
        var students = await crm.ListStudentsOfUserAsync(userId, ct);
        return students.Count == 0 ? [] : await ListCreditAccountsAsync(students.Select(s => s.Id).ToList(), ct);
    }

    public async Task<IReadOnlyList<PackageView>> ListPackagesAsync(bool includeInactive, CancellationToken ct) =>
        (await db.Packages.AsNoTracking().Where(p => includeInactive || p.IsActive).OrderBy(p => p.Code).ToListAsync(ct)).Select(p => p.ToView()).ToList();

    public async Task<IReadOnlyList<PaymentView>> ListPaymentsAsync(Guid orderId, CancellationToken ct) =>
        (await db.Payments.AsNoTracking().Where(p => p.OrderId == orderId).OrderBy(p => p.ReceivedAt).ToListAsync(ct))
        .Select(p => p.ToView()).ToList();

    public static readonly IReadOnlySet<string> OrderSorts = new HashSet<string> { "createdAt", "amount", "status" };

    /// <summary>Orders filtered, sorted and paged (newest first by default).</summary>
    public async Task<Result<Page<OrderView>>> ListOrdersAsync(OrderFilter filter, PageRequest page, CancellationToken ct)
    {
        var query = db.Orders.AsNoTracking();
        if (filter.StudentId is { } id)
        {
            query = query.Where(o => o.StudentId == id);
        }

        if (!string.IsNullOrWhiteSpace(filter.Q))
        {
            // At most 1,000 matching students: a search that broad is refined by the person typing.
            var students = (await crm.SearchStudentIdsAsync(filter.Q, 1000, ct)).ToArray();
            query = query.Where(o => students.Contains(o.StudentId));
        }

        switch (filter.Status)
        {
            case null or "" or "all":
                break;
            case "unpaid":
                query = query.Where(o => o.Status == OrderStatus.PendingPayment && o.AmountReceived == 0);
                break;
            case "partial":
                query = query.Where(o => o.Status == OrderStatus.PendingPayment && o.AmountReceived > 0);
                break;
            case "paid":
                query = query.Where(o => o.Status == OrderStatus.Paid);
                break;
            case "refund_required":
                query = query.Where(o => o.Status == OrderStatus.RefundRequired);
                break;
            case "refunded":
                query = query.Where(o => o.Status == OrderStatus.Refunded);
                break;
            case "cancelled":
                query = query.Where(o => o.Status == OrderStatus.Cancelled);
                break;
            default:
                return Error.Validation("billing.order_filter_invalid");
        }

        if (filter.Branch is { } branch)
        {
            query = query.Where(o => o.BranchId == branch);
        }

        if (filter.Package is { } package)
        {
            query = query.Where(o => o.PackageId == package);
        }

        if (filter.Class is { } classId)
        {
            query = query.Where(o => o.ClassId == classId);
        }

        if (filter.CreatedFrom is { } from)
        {
            var start = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            query = query.Where(o => o.CreatedAt >= start);
        }

        if (filter.CreatedTo is { } to)
        {
            var end = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            query = query.Where(o => o.CreatedAt < end);
        }

        var ordered = (page.Sort, page.Descending) switch
        {
            ("amount", var desc) => desc ? query.OrderByDescending(o => o.Amount).ThenByDescending(o => o.Id) : query.OrderBy(o => o.Amount).ThenBy(o => o.Id),
            ("status", var desc) => desc ? query.OrderByDescending(o => o.Status).ThenByDescending(o => o.CreatedAt).ThenBy(o => o.Id) : query.OrderBy(o => o.Status).ThenByDescending(o => o.CreatedAt).ThenBy(o => o.Id),
            ("createdAt", false) => query.OrderBy(o => o.CreatedAt).ThenBy(o => o.Id),
            _ => query.OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id),
        };
        return (await ordered.ToPageAsync(page, ct)).Map(o => o.ToView());
    }

    /// <summary>
    /// Revenue recognized in [from, to] per branch: lessons taught (paid credits × unit amount, on the class's branch and
    /// the session's local date) plus what refunds kept or gave beyond the unused lessons (on the collecting branch and the
    /// refund's local date). Refunded unused lessons were never recognized, so they change nothing.
    /// </summary>
    public async Task<IReadOnlyList<RevenueLine>> RecognizedRevenueAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var lessons = await db.Ledger.AsNoTracking()
            .Where(l => l.EntryType == LedgerEntryType.Consume && l.SessionLocalDate >= from && l.SessionLocalDate <= to)
            .GroupBy(l => new { l.BranchId, l.Currency })
            .Select(g => new { g.Key.BranchId, g.Key.Currency, Lessons = -g.Sum(l => l.Credits), Amount = -g.Sum(l => l.Credits * l.UnitAmount) })
            .ToListAsync(ct);
        var refunds = await db.Refunds.AsNoTracking()
            .Where(r => r.LocalDate >= from && r.LocalDate <= to && r.RetainedAmount != 0)
            .GroupBy(r => new { r.BranchId, r.Currency })
            .Select(g => new { g.Key.BranchId, g.Key.Currency, Amount = g.Sum(r => r.RetainedAmount) })
            .ToListAsync(ct);
        return lessons.Select(l => (l.BranchId, l.Currency)).Concat(refunds.Select(r => (r.BranchId, r.Currency))).Distinct()
            .Select(key =>
            {
                var lesson = lessons.FirstOrDefault(l => l.BranchId == key.BranchId && l.Currency == key.Currency);
                var refund = refunds.FirstOrDefault(r => r.BranchId == key.BranchId && r.Currency == key.Currency)?.Amount ?? 0;
                var lessonAmount = lesson?.Amount ?? 0;
                return new RevenueLine(key.BranchId, key.Currency, lesson?.Lessons ?? 0, lessonAmount, refund, lessonAmount + refund);
            })
            .ToList();
    }

    /// <summary>Every line of an order's lesson ledger, oldest first.</summary>
    public async Task<IReadOnlyList<LedgerLineView>> ListLedgerAsync(Guid orderId, CancellationToken ct) =>
        (await db.Ledger.AsNoTracking().Where(l => l.OrderId == orderId).OrderBy(l => l.CreatedAt).ThenBy(l => l.Id).ToListAsync(ct))
        .Select(l => new LedgerLineView(l.Id, l.EntryType.ToString(), l.CreditKind.ToString(), l.Credits, l.UnitAmount, l.Currency, l.BranchId,
            l.SessionLocalDate, l.Reason, l.CreatedAt))
        .ToList();
}

public sealed record LedgerLineView(
    Guid Id, string EntryType, string CreditKind, decimal Credits, decimal UnitAmount, string Currency, Guid? BranchId, DateOnly? SessionLocalDate,
    string? Reason, DateTimeOffset CreatedAt);

public static class BillingMapping
{
    public static PackageView ToView(this Package p) => new(p.Id, p.Code, p.Name.Values, p.PaidCredits, p.BonusCredits, p.Price, p.Currency, p.IsActive);

    public static OrderView ToView(this Order o) =>
        new(o.Id, o.StudentId, o.PackageId, o.ClassId, o.Amount, o.AmountReceived, o.Currency, o.Status.ToString(), o.BranchId, o.AmountRefunded,
            o.CreatedAt);

    public static PaymentView ToView(this Payment p) =>
        new(p.Id, p.OrderId, p.Amount, p.Method, p.Reference, p.ReceivedAt, p.VoidedAt, p.ReceiptNo, ReceiptCodes.For(DocumentKinds.Receipt, p.ReceiptNo),
            p.ReceivedByName);

    public static CreditAccountDto ToDto(this CreditBalance b) =>
        new(b.OrderId, b.StudentId, b.EnrollmentId, b.PaidCredits, b.BonusCredits, b.Total, b.Currency);
}
