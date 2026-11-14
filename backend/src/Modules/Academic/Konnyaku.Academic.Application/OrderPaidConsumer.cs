using Konnyaku.Academic.Contracts;
using Konnyaku.Academic.Domain;
using Konnyaku.Application.Messaging;
using Konnyaku.Billing.Contracts;

namespace Konnyaku.Academic.Application;

/// <summary>Saga step: a paid order for a class becomes an active enrollment, or is rejected back to Billing.</summary>
public sealed class OrderPaidConsumer(IAcademicDbContext db, EnrollHandler enroll) : IIntegrationEventHandler<OrderPaidV1>
{
    public async Task HandleAsync(OrderPaidV1 @event, IntegrationEventContext context, CancellationToken cancellationToken)
    {
        if (@event.ClassId is not { } classId || !await db.TryAcceptAsync(context.EventId, nameof(OrderPaidConsumer), cancellationToken))
        {
            return;
        }

        var result = await enroll.HandleAsync(classId, new EnrollCommand(@event.StudentId, EnrollmentType.Regular, @event.OrderId), cancellationToken);
        if (!result.IsSuccess)
        {
            db.Publish(new EnrollmentRejectedV1(@event.OrderId, classId, @event.StudentId, result.Error!.Code));
        }

        // Saves the inbox mark (and the rejection when enrollment failed; a success was already committed).
        await db.SaveChangesAsync(cancellationToken);
    }
}
