import type { Instrumentation } from "next";

/**
 * Server-side observability of the web app (docs/trien-khai.md#giám-sát-và-cảnh-báo), off unless configured:
 * - traces over OTLP when OTEL_EXPORTER_OTLP_ENDPOINT is set (OTEL_SDK_DISABLED=true turns them off), joined to the
 *   API's traces through the `traceparent` header of `apiFetch`;
 * - error reports (Sentry or GlitchTip) when SENTRY_DSN is set.
 */
export async function register() {
  if (process.env.NEXT_RUNTIME !== "nodejs") return;
  const { startTelemetry, startErrorReporting } = await import("./observability/server");
  startTelemetry();
  startErrorReporting();
}

/** Errors of server rendering, route handlers and server actions. */
export const onRequestError: Instrumentation.onRequestError = async (error, request, context) => {
  if (process.env.NEXT_RUNTIME !== "nodejs") return;
  const { reportRequestError } = await import("./observability/server");
  await reportRequestError(error, request, context);
};
