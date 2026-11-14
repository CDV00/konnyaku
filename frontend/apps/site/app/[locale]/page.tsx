import { getTranslations, setRequestLocale } from "next-intl/server";
import { Link } from "@/i18n/navigation";

export default async function HomePage({ params }: { params: Promise<{ locale: string }> }) {
  const { locale } = await params;
  setRequestLocale(locale);
  const t = await getTranslations("site");
  const common = await getTranslations("common");
  return (
    <main>
      <h1>{t("title")}</h1>
      <p>{common("phase0")}</p>
      <Link href="/register">{t("register")}</Link>
    </main>
  );
}
