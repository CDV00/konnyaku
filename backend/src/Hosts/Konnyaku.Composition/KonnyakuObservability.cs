using System.Diagnostics;
using Konnyaku.Application.Tenancy;
using Konnyaku.Infrastructure.Observability;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Sentry;
using Sentry.Extensibility;
using Sentry.Extensions.Logging;

namespace Konnyaku.Composition;

/// <summary>
/// Traces and metrics (OpenTelemetry over OTLP) and error reporting (Sentry SDK, also accepted by GlitchTip) of the API
/// and Worker hosts. Both are off unless configured, so dev, tests and a stack without the monitoring profile run
/// exactly as before:
/// <list type="bullet">
/// <item><c>OTEL_EXPORTER_OTLP_ENDPOINT</c> (+ <c>OTEL_EXPORTER_OTLP_PROTOCOL</c>) turns telemetry on;
/// <c>OTEL_SDK_DISABLED=true</c> turns it off again.</item>
/// <item><c>Sentry:Dsn</c> (<c>Sentry__Dsn</c>) turns error reporting on.</item>
/// </list>
/// Personal data stays out: spans carry routes, ids and type names, not query strings, client addresses or user agents;
/// error events are scrubbed by <see cref="PersonalDataScrubber"/>. Runbook: docs/trien-khai.md#giám-sát-và-cảnh-báo.
/// </summary>
public static class KonnyakuObservability
{
    /// <summary>Image tag of the running build (compose passes <c>IMAGE_TAG</c>), reported with traces and errors.</summary>
    public static string Version(IConfiguration configuration) =>
        configuration["KONNYAKU_VERSION"] is { Length: > 0 } version ? version : "dev";

    public static bool TelemetryEnabled(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"])
        && !configuration.GetValue<bool>("OTEL_SDK_DISABLED");

    public static bool ErrorReportingEnabled(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration["Sentry:Dsn"]);

    /// <param name="serviceName"><c>konnyaku-api</c> or <c>konnyaku-worker</c> (dashboards and alerts filter on it).</param>
    /// <param name="http">The host serves HTTP (server spans and ASP.NET Core metrics).</param>
    public static IHostApplicationBuilder AddKonnyakuTelemetry(this IHostApplicationBuilder builder, string serviceName, bool http)
    {
        // Every log line knows its trace: Loki links a line to the trace in Tempo.
        builder.Logging.Configure(options => options.ActivityTrackingOptions =
            ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId);
        if (!TelemetryEnabled(builder.Configuration))
        {
            return builder;
        }

        var configuration = builder.Configuration;
        var ratio = Math.Clamp(configuration.GetValue("OTEL_TRACES_SAMPLER_ARG", 1.0), 0, 1);
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(serviceName, serviceVersion: Version(configuration), serviceInstanceId: Environment.MachineName)
                .AddAttributes([new("deployment.environment.name", builder.Environment.EnvironmentName.ToLowerInvariant())]))
            .WithTracing(tracing =>
            {
                tracing.SetSampler(new ParentBasedSampler(new RequestRootSampler(ratio)));
                // Npgsql and HttpClient (System.Net.Http) emit their own spans; Konnyaku: outbox, notifications, SMTP.
                tracing.AddSource(KonnyakuTelemetry.Name, "Npgsql", "System.Net.Http");
                if (http)
                {
                    tracing.AddAspNetCoreInstrumentation(options =>
                    {
                        // Container health probes every 15 s would drown real requests.
                        options.Filter = context => !context.Request.Path.StartsWithSegments("/health");
                        options.RecordException = false;
                        options.EnrichWithHttpRequest = (activity, _) =>
                        {
                            // Search boxes put names and phone numbers in query strings; IP and user agent identify people.
                            activity.SetTag("url.query", null);
                            activity.SetTag("client.address", null);
                            activity.SetTag("client.port", null);
                            activity.SetTag("user_agent.original", null);
                        };
                        options.EnrichWithHttpResponse = (activity, response) =>
                        {
                            if (response.HttpContext.RequestServices.GetService<ITenantContext>()?.TenantId is { } tenantId)
                            {
                                activity.SetTag("konnyaku.tenant_id", tenantId.ToString());
                            }
                        };
                    });
                }

                tracing.AddOtlpExporter();
            })
            .WithMetrics(metrics =>
            {
                metrics.AddMeter(KonnyakuTelemetry.Name, "System.Runtime", "Npgsql", "System.Net.Http");
                if (http)
                {
                    // Built into ASP.NET Core: http.server.request.duration by route and status, Kestrel connections,
                    // rate-limiter rejections, unhandled exceptions.
                    metrics.AddMeter("Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Server.Kestrel",
                        "Microsoft.AspNetCore.RateLimiting", "Microsoft.AspNetCore.Diagnostics");
                }

                metrics.AddOtlpExporter();
            });
        return builder;
    }

    /// <summary>Error reporting of the API: unhandled exceptions and <c>LogError</c> become Sentry events.</summary>
    public static WebApplicationBuilder AddKonnyakuErrorReporting(this WebApplicationBuilder builder)
    {
        if (ErrorReportingEnabled(builder.Configuration))
        {
            builder.UseSentry(options =>
            {
                Configure(options, builder.Configuration, builder.Environment);
                options.MaxRequestBodySize = RequestSize.None;
            });
        }

        return builder;
    }

    /// <summary>Error reporting of the Worker: <c>LogError</c> (failed outbox messages, jobs) becomes a Sentry event.</summary>
    public static IHostApplicationBuilder AddKonnyakuErrorReporting(this IHostApplicationBuilder builder)
    {
        if (ErrorReportingEnabled(builder.Configuration))
        {
            builder.UseSentry(options => Configure(options, builder.Configuration, builder.Environment));
        }

        return builder;
    }

    private static void Configure(SentryHostOptions options, IConfiguration configuration, IHostEnvironment environment)
    {
        options.Dsn = configuration["Sentry:Dsn"];
        options.Environment = configuration["Sentry:Environment"] is { Length: > 0 } name
            ? name
            : environment.EnvironmentName.ToLowerInvariant();
        options.Release = Version(configuration);
        options.SendDefaultPii = false;
        options.AttachStacktrace = true;
        // Performance traces go to Tempo (OpenTelemetry); Sentry only receives errors.
        options.TracesSampleRate = 0;
        options.MinimumEventLevel = LogLevel.Error;
        options.MinimumBreadcrumbLevel = LogLevel.Information;
        options.SetBeforeSend((sentryEvent, _) => PersonalDataScrubber.Scrub(sentryEvent));
        options.SetBeforeBreadcrumb((breadcrumb, _) => PersonalDataScrubber.Scrub(breadcrumb));
    }
}

/// <summary>
/// Roots of traces: requests (server spans), outbox and notification work. A client span without a parent is a
/// background poll (the outbox and queue loops query PostgreSQL every few seconds) and is not recorded.
/// </summary>
public sealed class RequestRootSampler(double ratio) : Sampler
{
    private readonly TraceIdRatioBasedSampler _ratio = new(ratio);

    public override SamplingResult ShouldSample(in SamplingParameters samplingParameters) =>
        samplingParameters.Kind == ActivityKind.Client
            ? new SamplingResult(SamplingDecision.Drop)
            : _ratio.ShouldSample(samplingParameters);
}
