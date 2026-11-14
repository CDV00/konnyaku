using Konnyaku.Academic.Contracts;
using Konnyaku.Application.Messaging;
using Konnyaku.Application.Persistence;
using Konnyaku.Billing.Contracts;
using Konnyaku.Billing.Domain;
using Konnyaku.Crm.Contracts;
using Konnyaku.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Konnyaku.Billing.Application;

public interface IBillingDbContext
{
    DbSet<Package> Packages { get; }
    DbSet<Order> Orders { get; }
    DbSet<Payment> Payments { get; }
    DbSet<CreditLedgerEntry> Ledger { get; }
    DbSet<CreditBalance> Balances { get; }

    void Publish(IIntegrationEvent integrationEvent, string? correlationId = null);

    Task<bool> TryAcceptAsync(Guid eventId, string consumer, CancellationToken cancellationToken);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

public sealed record CreatePackageCommand(string Code, IReadOnlyDictionary<string, string> Name, decimal PaidCredits, decimal BonusCredits, decimal Price, string Currency);

public sealed record PackageView(Guid Id, string Code, IReadOnlyDictionary<string, string> Name, decimal PaidCredits, decimal BonusCredits, decimal Price, string Currency);

public sealed record CreateOrderCommand(Guid StudentId, Guid PackageId, Guid? ClassId, Guid? BranchId);

public sealed record OrderView(Guid Id, Guid StudentId, Guid PackageId, Guid? ClassId, decimal Amount, decimal AmountReceived, string Currency, string Status);

public sealed record RecordPaymentCommand(decimal Amount, string Method, string? Reference);

public sealed class OrderHandlers(IBillingDbContext db, ICrmApi crm, IAcademicApi academic, TimeProvider clock)
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
        if (await crm.GetStudentAsync(command.StudentId, ct) is null)
        {
            return Error.Validation("billing.student_unknown");
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

    /// <summary>Records money received. When the order becomes fully paid, its credits are opened in the ledger.</summary>
    public async Task<Result<OrderView>> RecordPaymentAsync(Guid orderId, RecordPaymentCommand command, CancellationToken ct)
    {
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null)
        {
            return Error.NotFound("billing.order_not_found");
        }

        var now = clock.GetUtcNow();
        var paid = order.ReceivePayment(command.Amount, now);
        if (!paid.IsSuccess)
        {
            return paid.Error!;
        }

        db.Payments.Add(new Payment(order.Id, command.Amount, string.IsNullOrWhiteSpace(command.Method) ? "cash" : command.Method, command.Reference, now));
        if (paid.Value)
        {
            var (account, lines) = CreditBalance.Open(order, now);
            db.Balances.Add(account);
            db.Ledger.AddRange(lines);
            db.Publish(new OrderPaidV1(order.Id, order.StudentId, order.ClassId, order.PaidCredits, order.BonusCredits));
            db.Publish(new CreditBalanceChangedV1(order.Id, order.StudentId, null, account.PaidCredits, account.BonusCredits));
        }

        await db.SaveChangesAsync(ct);
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

public sealed class AdjustCreditsHandler(IBillingDbContext db, TimeProvider clock)
{
    public async Task<Result<CreditAccountDto>> HandleAsync(Guid orderId, AdjustCreditsCommand command, CancellationToken ct)
    {
        if (command.Credits == 0 || string.IsNullOrWhiteSpace(command.Reason))
        {
            return Error.Validation("billing.adjustment_invalid");
        }

        var account = await db.Balances.FirstOrDefaultAsync(b => b.OrderId == orderId, ct);
        if (account is null)
        {
            return Error.NotFound("billing.account_not_found");
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

public sealed record RevenueLine(Guid? BranchId, string Currency, decimal Lessons, decimal Amount);

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

    public async Task<IReadOnlyList<OrderView>> ListOrdersAsync(Guid? studentId, CancellationToken ct)
    {
        var query = db.Orders.AsNoTracking();
        if (studentId is { } id)
        {
            query = query.Where(o => o.StudentId == id);
        }

        return (await query.OrderByDescending(o => o.CreatedAt).Take(500).ToListAsync(ct)).Select(o => o.ToView()).ToList();
    }

    /// <summary>Revenue recognized when lessons are taught: per branch of the class, for sessions held in [from, to].</summary>
    public async Task<IReadOnlyList<RevenueLine>> RecognizedRevenueAsync(DateOnly from, DateOnly to, CancellationToken ct) =>
        await db.Ledger.AsNoTracking()
            .Where(l => l.EntryType == LedgerEntryType.Consume && l.SessionLocalDate >= from && l.SessionLocalDate <= to)
            .GroupBy(l => new { l.BranchId, l.Currency })
            .Select(g => new RevenueLine(g.Key.BranchId, g.Key.Currency, -g.Sum(l => l.Credits), -g.Sum(l => l.Credits * l.UnitAmount)))
            .ToListAsync(ct);
}

public static class BillingMapping
{
    public static PackageView ToView(this Package p) => new(p.Id, p.Code, p.Name.Values, p.PaidCredits, p.BonusCredits, p.Price, p.Currency);

    public static OrderView ToView(this Order o) =>
        new(o.Id, o.StudentId, o.PackageId, o.ClassId, o.Amount, o.AmountReceived, o.Currency, o.Status.ToString());

    public static CreditAccountDto ToDto(this CreditBalance b) =>
        new(b.OrderId, b.StudentId, b.EnrollmentId, b.PaidCredits, b.BonusCredits, b.Total, b.Currency);
}
