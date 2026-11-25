using Konnyaku.Application.Privacy;
using Microsoft.EntityFrameworkCore;

namespace Konnyaku.Billing.Application;

/// <summary>A student's orders, payments, credit balances and lesson ledger for the data subject export.</summary>
public sealed class BillingPersonalData(IBillingDbContext db) : IPersonalDataSource
{
    public string Section => "billing";

    public async Task<object?> ExportAsync(PersonalDataSubject subject, CancellationToken cancellationToken)
    {
        var orders = await db.Orders.AsNoTracking().Where(o => o.StudentId == subject.StudentId).OrderBy(o => o.CreatedAt)
            .Select(o => new { o.Id, o.PackageId, o.ClassId, o.Amount, o.AmountReceived, o.Currency, status = o.Status.ToString(), o.CreatedAt, o.PaidAt })
            .ToListAsync(cancellationToken);
        var orderIds = orders.Select(o => o.Id).ToArray();
        var payments = await db.Payments.AsNoTracking().Where(p => orderIds.Contains(p.OrderId)).OrderBy(p => p.ReceivedAt)
            .Select(p => new { p.Id, p.OrderId, p.Amount, p.Method, p.Reference, p.ReceivedAt, p.VoidedAt })
            .ToListAsync(cancellationToken);
        var ledger = await db.Ledger.AsNoTracking().Where(l => l.StudentId == subject.StudentId).OrderBy(l => l.CreatedAt)
            .Select(l => new
            {
                l.Id, l.OrderId, type = l.EntryType.ToString(), kind = l.CreditKind.ToString(), l.Credits, l.SessionLocalDate, l.Reason, l.CreatedAt,
            })
            .ToListAsync(cancellationToken);
        return orders.Count == 0 && ledger.Count == 0 ? null : new { orders, payments, ledger };
    }
}
