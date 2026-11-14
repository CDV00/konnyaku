import { getTranslations } from "next-intl/server";

/** Stand-in for a Google Meet room when the tenant uses the Fake provider (development, CI, demos). */
export default async function DevMeetingPage({ params }: { params: Promise<{ code: string }> }) {
  const { code } = await params;
  const t = await getTranslations("meeting");
  return (
    <main style={{ maxWidth: 560, margin: "48px auto", fontFamily: "system-ui" }}>
      <h1>{t("title")}</h1>
      <p>{t("body", { code })}</p>
    </main>
  );
}
