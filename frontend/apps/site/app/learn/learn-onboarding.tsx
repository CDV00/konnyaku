import { getTranslations } from "next-intl/server";
import { requireMe } from "@konnyaku/session";
import { Onboarding } from "@konnyaku/ui/kk/onboarding";
import { guideUrl } from "../../guide-url";

const steps = ["credits", "schedule", "join", "replay", "password"] as const;
const links: Partial<Record<(typeof steps)[number], string>> = { password: "/learn/change-password" };

/** First-run guide on the /learn home page for students and parents. */
export async function LearnOnboarding() {
  const { me } = await requireMe("learn");
  const t = await getTranslations("onboarding");
  return (
    <Onboarding
      storageKey={`learn:${me.userId}`}
      title={t("learn.title")}
      intro={t("learn.intro")}
      steps={steps.map((step) => ({
        key: step,
        title: t(`learn.steps.${step}.title`),
        description: t(`learn.steps.${step}.description`),
        href: links[step],
      }))}
      labels={{ show: t("show"), hide: t("hide") }}
      guide={{ href: guideUrl("hoc-vien"), label: t("guide") }}
    />
  );
}
