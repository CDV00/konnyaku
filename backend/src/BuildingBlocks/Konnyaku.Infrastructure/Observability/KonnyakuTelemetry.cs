using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Konnyaku.Infrastructure.Observability;

/// <summary>
/// Spans and metrics of Konnyaku's own work (outbox dispatch, notification queue, email). Plain
/// <see cref="System.Diagnostics"/> types: they cost nothing unless the host exports them (OpenTelemetry, see
/// <c>KonnyakuObservability</c> in Konnyaku.Composition). Attributes carry ids and type names only, never personal data
/// (names, emails, phone numbers, tokens).
/// </summary>
public static class KonnyakuTelemetry
{
    public const string Name = "Konnyaku";

    public static readonly ActivitySource Source = new(Name);

    public static readonly Meter Meter = new(Name);

    public static readonly Counter<long> OutboxProcessed = Meter.CreateCounter<long>(
        "konnyaku.outbox.processed", "{message}", "Integration events dispatched from a module outbox, by result.");

    public static readonly Histogram<double> OutboxLag = Meter.CreateHistogram<double>(
        "konnyaku.outbox.lag", "s", "Time between publishing an integration event and dispatching it.");

    public static readonly Counter<long> EmailsSent = Meter.CreateCounter<long>(
        "konnyaku.email.sent", "{email}", "Emails handed to the SMTP server, by result (sent, transient, permanent).");

    private static readonly ConcurrentDictionary<string, OutboxSnapshot> Outboxes = new();
    private static readonly ConcurrentDictionary<string, QueueSnapshot> NotificationQueue = new();

    static KonnyakuTelemetry()
    {
        Meter.CreateObservableGauge("konnyaku.outbox.pending", () => Outboxes.Select(o =>
            new Measurement<long>(o.Value.Pending, new KeyValuePair<string, object?>("module", o.Key))),
            "{message}", "Outbox messages waiting to be dispatched (still retried).");
        Meter.CreateObservableGauge("konnyaku.outbox.dead", () => Outboxes.Select(o =>
            new Measurement<long>(o.Value.Dead, new KeyValuePair<string, object?>("module", o.Key))),
            "{message}", "Outbox messages that failed every attempt and are no longer retried.");
        Meter.CreateObservableGauge("konnyaku.outbox.oldest_age", () => Outboxes.Select(o =>
            new Measurement<double>(o.Value.OldestAgeSeconds, new KeyValuePair<string, object?>("module", o.Key))),
            "s", "Age of the oldest outbox message waiting to be dispatched (0 when empty).");
        Meter.CreateObservableGauge("konnyaku.notification.queued", () => NotificationQueue.Select(q =>
            new Measurement<long>(q.Value.Queued, new KeyValuePair<string, object?>("kind", q.Key))),
            "{item}", "Notification work in the dispatch queue (deliveries incl. retries, reminders), by kind.");
        Meter.CreateObservableGauge("konnyaku.notification.overdue_age", () => NotificationQueue.Select(q =>
            new Measurement<double>(q.Value.OverdueSeconds, new KeyValuePair<string, object?>("kind", q.Key))),
            "s", "How long the most overdue notification work has been due (0 when nothing is late).");
    }

    /// <summary>Latest outbox backlog of one module, sampled by the process that dispatches it.</summary>
    public static void RecordOutbox(string module, long pending, long dead, double oldestAgeSeconds) =>
        Outboxes[module] = new OutboxSnapshot(pending, dead, oldestAgeSeconds);

    /// <summary>Latest notification queue, replacing the previous sample (kinds that disappeared report 0).</summary>
    public static void RecordNotificationQueue(IReadOnlyDictionary<string, (long Queued, double OverdueSeconds)> byKind)
    {
        foreach (var kind in NotificationQueue.Keys.Except(byKind.Keys))
        {
            NotificationQueue[kind] = new QueueSnapshot(0, 0);
        }

        foreach (var (kind, value) in byKind)
        {
            NotificationQueue[kind] = new QueueSnapshot(value.Queued, value.OverdueSeconds);
        }
    }

    /// <summary>
    /// Parent of work that continues a request later (outbox): the W3C <c>traceparent</c> stored with the message,
    /// so dispatch spans join the trace of the request that published the event.
    /// </summary>
    public static ActivityContext ParentFrom(string? traceparent) =>
        traceparent is not null && ActivityContext.TryParse(traceparent, null, out var context) ? context : default;

    private sealed record OutboxSnapshot(long Pending, long Dead, double OldestAgeSeconds);

    private sealed record QueueSnapshot(long Queued, double OverdueSeconds);
}
