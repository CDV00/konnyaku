using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Konnyaku.Infrastructure.Messaging;

/// <summary>Event already handled by a consumer of this module; makes at-least-once delivery idempotent.</summary>
public sealed class InboxMessage(Guid eventId, string consumer, DateTimeOffset processedAt)
{
    public Guid EventId { get; private set; } = eventId;
    public string Consumer { get; private set; } = consumer;
    public DateTimeOffset ProcessedAt { get; private set; } = processedAt;
}

internal sealed class InboxMessageConfiguration : IEntityTypeConfiguration<InboxMessage>
{
    public void Configure(EntityTypeBuilder<InboxMessage> builder)
    {
        builder.ToTable("inbox_message");
        builder.HasKey(m => new { m.EventId, m.Consumer });
        builder.Property(m => m.Consumer).HasMaxLength(200);
    }
}
