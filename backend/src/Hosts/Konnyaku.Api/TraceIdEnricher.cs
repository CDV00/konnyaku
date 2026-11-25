using Serilog.Core;
using Serilog.Events;

namespace Konnyaku.Api;

/// <summary>
/// Adds <c> trace_id=…</c> to log lines written inside a traced request, so Grafana (Loki) links a line to its trace in
/// Tempo. Lines outside a request render as before.
/// </summary>
public sealed class TraceIdEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        if (logEvent.TraceId is { } traceId)
        {
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("TraceSuffix", $" trace_id={traceId.ToHexString()}"));
        }
    }
}
