import enApps from "../messages/en/apps.json";
import enCommon from "../messages/en/common.json";
import viApps from "../messages/vi/apps.json";
import viCommon from "../messages/vi/common.json";

/** UI locales supported by the platform. A tenant enables a subset (Phase 1). */
export const locales = ["vi", "en"] as const;
export type Locale = (typeof locales)[number];
export const defaultLocale: Locale = "vi";
export const localeCookie = "NEXT_LOCALE";

const catalogs = {
  vi: { common: viCommon, ...viApps },
  en: { common: enCommon, ...enApps },
} satisfies Record<Locale, unknown>;

export type Messages = (typeof catalogs)["vi"];

export function isLocale(value: unknown): value is Locale {
  return typeof value === "string" && (locales as readonly string[]).includes(value);
}

export function loadMessages(locale: Locale): Messages {
  return catalogs[locale];
}

/** Picks the best supported locale from an Accept-Language header (exact tag, then language). */
export function matchLocale(acceptLanguage: string | null | undefined): Locale {
  const requested = (acceptLanguage ?? "")
    .split(",")
    .map((part) => {
      const [tag = "", q] = part.trim().split(";q=");
      return { tag: tag.toLowerCase(), q: q ? Number(q) : 1 };
    })
    .filter((x) => x.tag)
    .sort((a, b) => b.q - a.q);
  for (const { tag } of requested) {
    const language = tag.split("-")[0];
    if (isLocale(tag)) return tag;
    if (isLocale(language)) return language;
  }
  return defaultLocale;
}
