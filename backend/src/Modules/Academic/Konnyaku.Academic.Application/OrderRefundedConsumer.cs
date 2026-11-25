using Konnyaku.Academic.Domain;
using Konnyaku.Application.Messaging;
using Konnyaku.Billing.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Konnyaku.Academic.Application;

/// <summary>
/// A refunded order no longer pays for a seat: the enrollment it paid for is dropped (seat released, EnrollmentDroppedV1).
/// Idempotent: the inbox mark and the "still holds a seat" check both stop a second run.
/// </summary>
public sealed class OrderRefundedConsumer(IAcademicDbContext db, EnrollmentLifecycleHandler lifecycle) : IIntegrationEventHandler<OrderRefundedV1>
{
    public async Task HandleAsync(OrderRefundedV1 @event, IntegrationEventContext context, CancellationToken cancellationToken)
    {
        if (!await db.TryAcceptAsync(context.EventId, nameof(OrderRefundedConsumer), cancellationToken))
        {
            return;
        }

        var enrollmentId = await db.Enrollments.AsNoTracking()
            .Where(e => (e.Id == @event.EnrollmentId || e.OrderId == @event.OrderId) &&
                (e.Status == EnrollmentStatus.Active || e.Status == EnrollmentStatus.Frozen))
            .Select(e => (Guid?)e.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (enrollmentId is { } id)
        {
            // Saves the inbox mark in the same transaction as the drop.
            await lifecycle.DropAsync(id, cancellationToken);
            return;
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
