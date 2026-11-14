import { getTranslations, setRequestLocale } from "next-intl/server";

export default async function RegisterPage({ params }: { params: Promise<{ locale: string }> }) {
  const { locale } = await params;
  setRequestLocale(locale);
  const t = await getTranslations("site");
  return (
    <main>
      <h1>{t("register")}</h1>
    </main>
  );
}
