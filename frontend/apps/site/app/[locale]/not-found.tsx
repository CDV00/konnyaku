import { getLocale, getTranslations } from "next-intl/server";
import { Building2, FileQuestion } from "lucide-react";
import { tenantHost, tenantInfo } from "@konnyaku/session";
import { getPathname } from "@/i18n/navigation";
import { primaryAction, secondaryAction, StatusView } from "../status-view";

/** 404 of the public site, in the visitor's language; on a host that belongs to no center, says so instead. */
export default async function NotFound() {
  const locale = await getLocale();
  const t = await getTranslations("status");
  if (!(await tenantInfo())) {
    return (
      <main>
        <title>{t("unknownCenter.title")}</title>
        <StatusView
          icon={Building2}
          code="404"
          title={t("unknownCenter.title")}
          body={t("unknownCenter.body", { host: await tenantHost() })}
        />
      </main>
    );
  }
  return (
    <main>
      <title>{t("notFound.title")}</title>
      <StatusView icon={FileQuestion} code="404" title={t("notFound.title")} body={t("notFound.body")}>
        <a href={`/${locale}`} className={primaryAction}>{t("notFound.home")}</a>
        <a href={getPathname({ locale, href: "/register" })} className={secondaryAction}>{t("notFound.register")}</a>
      </StatusView>
    </main>
  );
}
