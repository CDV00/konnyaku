import type { ReactNode } from "react";
import type { Metadata } from "next";
import { hasLocale } from "next-intl";
import { getLocale, getTranslations, setRequestLocale } from "next-intl/server";
import { tenantInfo } from "@konnyaku/session";
import { routing } from "@/i18n/routing";
import { siteOrigin } from "../seo";
import { SiteChrome } from "../site-chrome";

export function generateStaticParams() {
  return routing.locales.map((locale) => ({ locale }));
}

/** Defaults for every public page: title template with the center name, indexable, metadataBase on the tenant host. */
export async function generateMetadata({ params }: { params: Promise<{ locale: string }> }): Promise<Metadata> {
  const { locale } = await params;
  const tenant = await tenantInfo();
  if (!hasLocale(routing.locales, locale) || !tenant) return { robots: { index: false, follow: false } };
  const t = await getTranslations({ locale, namespace: "status.seo" });
  return {
    metadataBase: new URL(await siteOrigin()),
    title: { default: tenant.name, template: `%s · ${tenant.name}` },
    description: t("homeDescription", { center: tenant.name }),
    applicationName: tenant.name,
    robots: { index: true, follow: true },
  };
}

/**
 * Root document of the public site. An unknown first segment (not a language) still gets the chrome, in the
 * visitor's language, so the page below can answer with the localized 404 (see guard.ts).
 */
export default async function LocaleLayout({
  children,
  params,
}: {
  children: ReactNode;
  params: Promise<{ locale: string }>;
}) {
  const { locale: segment } = await params;
  const locale = hasLocale(routing.locales, segment) ? segment : await getLocale();
  setRequestLocale(locale);
  return <SiteChrome locale={locale}>{children}</SiteChrome>;
}
