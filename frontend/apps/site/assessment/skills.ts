import { apiFetch } from "@konnyaku/session";
import type { Framework } from "./types";

/** Skill code → name in the UI locale, across the Catalog frameworks (skill codes repeat with the same meaning). */
export async function skillNames(token: string, locale: string, language?: string): Promise<Map<string, string>> {
  const response = await apiFetch(`/api/catalog/frameworks${language ? `?language=${language}` : ""}`, { token });
  const frameworks = response.ok ? ((await response.json()) as Framework[]) : [];
  const names = new Map<string, string>();
  for (const framework of frameworks) {
    for (const skill of framework.skills) {
      if (!names.has(skill.code)) names.set(skill.code, skill.name[locale] ?? skill.name.vi ?? skill.code);
    }
  }
  return names;
}
