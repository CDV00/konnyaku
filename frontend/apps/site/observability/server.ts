import { hostname } from "node:os";
import { trace, type Attributes } from "@opentelemetry/api";
import type { Context } from "@opentelemetry/api";
import type { ReadableSpan, Span, SpanProcessor } from "@opentelemetry/sdk-trace-base";
import {
  captureException,
  createStackParser,
  createTransport,
  dedupeIntegration,
  functionToStringIntegration,
  linkedErrorsIntegration,
  setCurrentClient,
  withScope,
  type ErrorEvent,
} from "@sentry/core";
import { nodeStackLineParser, ServerRuntimeClient } from "@sentry/core/server";
import { registerOTel } from "@vercel/otel";
import type { Instrumentation } from "next";
import { droppedAttributes, scrubText, urlAttributes, withoutQuery } from "./scrub";

const apiUrl = process.env.KONNYAKU_API_URL ?? "http://localhost:5080";
const version = process.env.KONNYAKU_VERSION ?? "dev";
const sentryDsn = process.env.SENTRY_DSN ?? "";

/** Removes personal data from every span before it is exported (runs before the exporting processor). */
class ScrubSpanProcessor implements SpanProcessor {
  onStart(_span: Span, _parentContext: Context): void {}

  onEnd(span: ReadableSpan): void {
    const attributes = span.attributes as Attributes;
    for (const key of droppedAttributes) delete attributes[key];
    for (const key of urlAttributes) {
      const value = attributes[key];
      if (typeof value === "string") attributes[key] = withoutQuery(value);
    }
    // Ended spans are read-only through the API; the name field itself is still a plain property.
    if (span.name.includes("?")) (span as { name: string }).name = withoutQuery(span.name);
  }

  forceFlush(): Promise<void> {
    return Promise.resolve();
  }

  shutdown(): Promise<void> {
    return Promise.resolve();
  }
}

export function startTelemetry(): void {
  if (!process.env.OTEL_EXPORTER_OTLP_ENDPOINT || process.env.OTEL_SDK_DISABLED === "true") return;
  registerOTel({
    serviceName: "konnyaku-web",
    attributes: { "service.version": version, "service.instance.id": hostname() },
    spanProcessors: [new ScrubSpanProcessor(), "auto"],
    instrumentationConfig: {
      fetch: {
        // traceparent only to our own API, never to third parties.
        propagateContextUrls: [apiUrl],
        ignoreUrls: sentryDsn ? [new URL(sentryDsn).origin] : [],
      },
    },
  });
}

let reporting = false;

export function startErrorReporting(): void {
  if (!sentryDsn || reporting) return;
  const client = new ServerRuntimeClient({
    dsn: sentryDsn,
    release: version,
    environment: process.env.SENTRY_ENVIRONMENT || "production",
    platform: "node",
    runtime: { name: "node", version: process.version },
    sendDefaultPii: false,
    stackParser: createStackParser(nodeStackLineParser()),
    integrations: [dedupeIntegration(), linkedErrorsIntegration(), functionToStringIntegration()],
    // Breadcrumbs would collect fetched URLs (query strings) and console output.
    beforeBreadcrumb: () => null,
    beforeSend: scrubEvent,
    transport: (options) =>
      createTransport(options, async (request) => {
        const response = await fetch(options.url, {
          method: "POST",
          body: request.body as BodyInit,
          headers: options.headers,
        });
        return {
          statusCode: response.status,
          headers: {
            "x-sentry-rate-limits": response.headers.get("x-sentry-rate-limits"),
            "retry-after": response.headers.get("retry-after"),
          },
        };
      }),
  });
  setCurrentClient(client);
  client.init();
  reporting = true;
}

function scrubEvent(event: ErrorEvent): ErrorEvent {
  delete event.request;
  delete event.user;
  delete event.server_name;
  if (event.message) event.message = scrubText(event.message);
  for (const exception of event.exception?.values ?? []) exception.value = scrubText(exception.value);
  return event;
}

/** The route pattern, not the URL (no ids or query), plus the trace id to open the trace in Grafana. */
export async function reportRequestError(
  error: unknown,
  request: Parameters<Instrumentation.onRequestError>[1],
  context: Parameters<Instrumentation.onRequestError>[2],
): Promise<void> {
  if (!reporting) return;
  withScope((scope) => {
    scope.setTag("route", context.routePath);
    scope.setTag("route_type", context.routeType);
    scope.setTag("router", context.routerKind);
    scope.setTag("method", request.method);
    const traceId = trace.getActiveSpan()?.spanContext().traceId;
    if (traceId) scope.setTag("trace_id", traceId);
    captureException(error);
  });
}
