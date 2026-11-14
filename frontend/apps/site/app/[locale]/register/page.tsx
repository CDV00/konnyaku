import { getTranslations, setRequestLocale } from "next-intl/server";
import { apiFetch } from "@konnyaku/session";
import { RegisterForm } from "./register-form";

type Language = { code: string; name: Record<string, string> };

export default async function RegisterPage({ params }: { params: Promise<{ locale: string }> }) {
  const { locale } = await params;
  setRequestLocale(locale);
  const t = await getTranslations("register");
  const response = await apiFetch("/api/catalog/languages");
  const languages = response.ok ? ((await response.json()) as Language[]) : [];
  const options = languages.map((l) => ({ code: l.code, label: l.name[locale] ?? l.name.en ?? l.code }));
  return (
    <main style={{ maxWidth: 560, margin: "32px auto", fontFamily: "system-ui" }}>
      <h1>{t("title")}</h1>
      <p>{t("intro")}</p>
      <RegisterForm languages={options} locale={locale} />
    </main>
  );
}
