/** Helpers shared by the teacher authoring screens (banks, papers). The hook is for client components only. */
import { useTranslations } from "next-intl";
import { errorKey } from "@/assessment/types";

/** Catalog language (`/api/catalog/languages`). */
export type Language = { code: string; name: Record<string, string> };

/** Name in the UI locale, else Vietnamese, else any. */
export function localizedName(name: Record<string, string> | null | undefined, locale: string): string {
  if (!name) return "";
  return name[locale] ?? name.vi ?? Object.values(name)[0] ?? "";
}

/** Short id for options, targets, sections, rubrics (unique inside one question or paper). */
export function newId(prefix: string, taken: readonly string[] = []): string {
  for (;;) {
    const id = `${prefix}${Math.random().toString(36).slice(2, 7)}`;
    if (!taken.includes(id)) return id;
  }
}

/** Splits "a, b ,c" into trimmed, non-empty values. */
export function splitList(value: string, separator: RegExp = /[,，、]/): string[] {
  return value
    .split(separator)
    .map((x) => x.trim())
    .filter((x) => x.length > 0);
}

/** Translates an API error code (with its params) in the `assessmentAuthoring.errors` catalog. */
export function useErrorText() {
  const t = useTranslations("assessmentAuthoring");
  return (code: string, params: Record<string, unknown> = {}) => {
    const key = `errors.${errorKey(code)}`;
    const values = Object.fromEntries(Object.entries(params).filter(([, v]) => typeof v === "number" || typeof v === "string")) as Record<
      string,
      string | number
    >;
    return t.has(key) ? t(key as "errors.unknown", values) : t("errors.unknown");
  };
}
