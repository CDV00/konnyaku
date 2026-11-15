import { getTranslations } from "next-intl/server";
import { MonitorPlay } from "lucide-react";

/** Stand-in for a Google Meet room when the tenant uses the Fake provider (development, CI, demos). */
export default async function DevMeetingPage({ params }: { params: Promise<{ code: string }> }) {
  const { code } = await params;
  const t = await getTranslations("meeting");
  return (
    <main className="mx-auto flex max-w-xl flex-col items-center gap-4 px-4 py-20 text-center">
      <span className="grid size-16 place-items-center rounded-2xl bg-primary/10 text-primary">
        <MonitorPlay className="size-8" />
      </span>
      <h1 className="text-2xl font-semibold tracking-tight">{t("title")}</h1>
      <p className="text-muted-foreground">{t("body", { code })}</p>
      <code className="rounded-lg bg-muted px-3 py-1.5 font-mono text-sm">{code}</code>
    </main>
  );
}
