using Konnyaku.Application.Messaging;
using Konnyaku.Learning.Contracts;

namespace Konnyaku.Billing.Application;

/// <summary>A confirmed, billable attendance spends one lesson (idempotent per attendance).</summary>
public sealed class AttendanceConfirmedConsumer(ConsumeCreditHandler consume) : IIntegrationEventHandler<AttendanceConfirmedV1>
{
    public async Task HandleAsync(AttendanceConfirmedV1 @event, IntegrationEventContext context, CancellationToken cancellationToken)
    {
        if (!@event.Billable)
        {
            return;
        }

        await consume.HandleAsync(new ConsumeCreditCommand(@event.EnrollmentId, @event.AttendanceId, @event.BranchId, @event.LocalDate), cancellationToken);
    }
}
