import { getTranslations } from "next-intl/server";

export default async function HomePage() {
  const t = await getTranslations("teach");
  const common = await getTranslations("common");
  return (
    <main>
      <h1>{t("title")}</h1>
      <p>{common("phase0")}</p>
    </main>
  );
}
