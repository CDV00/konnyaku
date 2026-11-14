import { getLocale, getTranslations, setRequestLocale } from "next-intl/server";
import { apiFetch } from "@konnyaku/session";
import NextLink from "next/link";
import { Link } from "@/i18n/navigation";

type Program = {
  id: string;
  code: string;
  name: Record<string, string>;
  languageCode: string;
  frameworkCode: string;
  levels: { id: string }[];
};

function localized(value: Record<string, string>, locale: string): string {
  return value[locale] ?? value[locale.split("-")[0] ?? ""] ?? Object.values(value)[0] ?? "";
}

export default async function HomePage({ params }: { params: Promise<{ locale: string }> }) {
  const { locale } = await params;
  setRequestLocale(locale);
  const t = await getTranslations("site");
  const response = await apiFetch("/api/academic/public/programs");
  const programs = response.ok ? ((await response.json()) as Program[]) : [];
  const uiLocale = await getLocale();
  return (
    <main style={{ maxWidth: 720, margin: "32px auto", fontFamily: "system-ui" }}>
      <h1>{t("title")}</h1>
      <h2>{t("programs")}</h2>
      {programs.length === 0 ? (
        <p>{t("noPrograms")}</p>
      ) : (
        <ul>
          {programs.map((p) => (
            <li key={p.id}>
              {localized(p.name, uiLocale)} · {p.frameworkCode} · {t("levels", { count: p.levels.length })}
            </li>
          ))}
        </ul>
      )}
      <p>
        <Link href="/register">{t("register")}</Link> | <NextLink href="/login">{t("login")}</NextLink>
      </p>
    </main>
  );
}
