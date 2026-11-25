import { getTranslations } from "next-intl/server";
import { requireMe } from "@konnyaku/session";
import { Onboarding } from "@konnyaku/ui/kk/onboarding";
import { guideUrl } from "../../guide-url";

const steps = ["schedule", "join", "attendance", "timesheet"] as const;

/** First-run guide on the /teach home page: how a lesson goes from schedule to attendance and pay. */
export async function TeachOnboarding() {
  const { me } = await requireMe("staff");
  const t = await getTranslations("onboarding");
  return (
    <Onboarding
      storageKey={`teach:${me.userId}`}
      title={t("teach.title")}
      intro={t("teach.intro")}
      steps={steps.map((step) => ({ key: step, title: t(`teach.steps.${step}.title`), description: t(`teach.steps.${step}.description`) }))}
      labels={{ show: t("show"), hide: t("hide") }}
      guide={{ href: guideUrl("giao-vien"), label: t("guide") }}
    />
  );
}
