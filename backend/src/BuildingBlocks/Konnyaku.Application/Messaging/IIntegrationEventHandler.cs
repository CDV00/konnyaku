using Konnyaku.SharedKernel;

namespace Konnyaku.Application.Messaging;

public sealed record IntegrationEventContext(Guid EventId, Guid TenantId, DateTimeOffset OccurredAt, string? CorrelationId);

/// <summary>
/// Handles an integration event from another module. Delivery is at-least-once: handlers must be idempotent
/// (use the inbox of their own module).
/// </summary>
public interface IIntegrationEventHandler<in TEvent> where TEvent : IIntegrationEvent
{
    Task HandleAsync(TEvent @event, IntegrationEventContext context, CancellationToken cancellationToken);
}
