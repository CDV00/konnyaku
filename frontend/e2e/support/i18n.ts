import enApps from "../../packages/i18n/messages/en/apps.json" with { type: "json" };
import enBooking from "../../packages/i18n/messages/en/booking.json" with { type: "json" };
import enPlatform from "../../packages/i18n/messages/en/platform.json" with { type: "json" };
import viApps from "../../packages/i18n/messages/vi/apps.json" with { type: "json" };
import viBooking from "../../packages/i18n/messages/vi/booking.json" with { type: "json" };
import viPlatform from "../../packages/i18n/messages/vi/platform.json" with { type: "json" };

/**
 * UI text straight from the i18n catalog, so a reworded label does not break a test while a missing one still does.
 * `t("login.submit")`, `t("admin.billing.refund")`, `t("session.greeting", { name })` (simple `{name}` arguments only).
 */
const catalogs = {
  vi: { ...viApps, ...viBooking, platform: viPlatform },
  en: { ...enApps, ...enBooking, platform: enPlatform },
} as const;

export type Locale = keyof typeof catalogs;

export function t(key: string, params: Record<string, string | number> = {}, locale: Locale = "vi"): string {
  let node: unknown = catalogs[locale];
  for (const part of key.split(".")) {
    node = (node as Record<string, unknown> | undefined)?.[part];
  }
  if (typeof node !== "string") throw new Error(`no message ${locale}:${key}`);
  return node.replace(/\{(\w+)\}/g, (whole, name: string) => (name in params ? String(params[name]) : whole));
}

/** A message as a pattern: the text around `{arguments}` must match, the arguments may be anything. */
export function tPattern(key: string, locale: Locale = "vi"): RegExp {
  const parts = t(key, {}, locale).split(/\{\w+\}/);
  return new RegExp(parts.map((p) => p.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")).join(".*"));
}
