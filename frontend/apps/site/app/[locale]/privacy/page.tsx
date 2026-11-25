import type { Metadata } from "next";
import { hasLocale } from "next-intl";
import { getTranslations } from "next-intl/server";
import { tenantInfo } from "@konnyaku/session";
import { routing } from "@/i18n/routing";
import { pageMetadata } from "../../seo";
import { publicLocale } from "../guard";
import { LegalDocument } from "../legal-document";

export async function generateMetadata({ params }: { params: Promise<{ locale: string }> }): Promise<Metadata> {
  const { locale } = await params;
  if (!hasLocale(routing.locales, locale)) return {};
  const t = await getTranslations({ locale, namespace: "legal.privacy" });
  const tenant = await tenantInfo();
  const center = tenant?.name ?? "";
  return pageMetadata({ locale, href: "/privacy", title: t("title"), description: t("description", { center }), tenant });
}

export default async function Page({ params }: { params: Promise<{ locale: string }> }) {
  const locale = await publicLocale(params);
  return <LegalDocument kind="privacy" locale={locale} />;
}
