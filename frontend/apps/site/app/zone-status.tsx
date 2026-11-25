import { getTranslations } from "next-intl/server";
import { FileQuestion } from "lucide-react";
import { primaryAction, StatusView } from "./status-view";

/** 404 inside a signed-in zone (learn, teach, ops): keeps the zone chrome, links back to the zone home. */
export async function ZoneNotFound({ home }: { home: string }) {
  const t = await getTranslations("status");
  return (
    <StatusView icon={FileQuestion} code="404" title={t("notFound.title")} body={t("notFound.body")}>
      <a href={home} className={primaryAction}>{t("notFound.zoneHome")}</a>
    </StatusView>
  );
}
