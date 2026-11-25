using System.Diagnostics;
using System.Text.Json;
using Konnyaku.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Konnyaku.Infrastructure.Messaging;

/// <summary>
/// Integration event waiting to be dispatched. Infrastructure table: no RLS (the dispatcher reads across tenants);
/// <see cref="TenantId"/> travels with the event and becomes the tenant context of every handler.
/// </summary>
public sealed class OutboxMessage
{
    public const int MaxAttempts = 10;

    internal static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private OutboxMessage()
    {
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string Type { get; private set; } = string.Empty;
    public string Payload { get; private set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; private set; }
    public DateTimeOffset? ProcessedAt { get; private set; }
    public int Attempts { get; private set; }
    public string? LastError { get; private set; }
    /// <summary>
    /// Given by the publisher, or else the W3C <c>traceparent</c> of the current request, so the dispatch joins its
    /// trace (<see cref="Observability.KonnyakuTelemetry.ParentFrom"/>).
    /// </summary>
    public string? CorrelationId { get; private set; }

    public static OutboxMessage Create(IIntegrationEvent integrationEvent, Guid tenantId, string? correlationId)
    {
        var type = integrationEvent.GetType();
        return new OutboxMessage
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Type = $"{type.FullName}, {type.Assembly.GetName().Name}",
            Payload = JsonSerializer.Serialize(integrationEvent, type, SerializerOptions),
            OccurredAt = DateTimeOffset.UtcNow,
            CorrelationId = correlationId ?? (Activity.Current is { IdFormat: ActivityIdFormat.W3C } current ? current.Id : null),
        };
    }

    public void MarkProcessed(DateTimeOffset at)
    {
        ProcessedAt = at;
        LastError = null;
    }

    public void MarkFailed(string error)
    {
        Attempts++;
        LastError = error.Length > 2000 ? error[..2000] : error;
    }
}

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_message");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Type).HasMaxLength(500);
        builder.Property(m => m.Payload).HasColumnType("jsonb");
        builder.Property(m => m.CorrelationId).HasMaxLength(100);
        builder.HasIndex(m => m.OccurredAt).HasFilter("processed_at IS NULL");
    }
}
