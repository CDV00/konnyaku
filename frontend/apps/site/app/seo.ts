import type { Metadata } from "next";
import { headers } from "next/headers";
import type { Locale } from "@konnyaku/i18n";
import type { TenantInfo } from "@konnyaku/session";
import { getPathname } from "@/i18n/navigation";
import { routing, type PublicHref } from "@/i18n/routing";

/** Paths search engines must not index: signed-in zones, sign-in, route handlers, meeting and classroom rooms, platform console. */
export const privatePaths = ["/learn", "/teach", "/ops", "/login", "/api", "/dev-meeting", "/room", "/platform"] as const;

/** Origin the visitor used (tenant host); https behind the proxy, http only for *.localhost in dev. */
export async function siteOrigin(): Promise<string> {
  const h = await headers();
  const host = h.get("x-forwarded-host") ?? h.get("host") ?? "localhost";
  const proto = h.get("x-forwarded-proto")?.split(",")[0]?.trim()
    ?? (/(^|\.)localhost(:\d+)?$/.test(host) ? "http" : "https");
  return `${proto}://${host}`;
}

/** Locales the center publishes (enabled for the tenant and supported by the site). */
export function publicLocales(tenant: TenantInfo | null): Locale[] {
  const enabled = routing.locales.filter((l) => tenant?.enabledLocales?.includes(l));
  return enabled.length > 0 ? enabled : [...routing.locales];
}

/** Absolute URL of a public page in every published language, plus x-default (the center's default language). */
export function alternates(origin: string, href: PublicHref, tenant: TenantInfo | null): Record<string, string> {
  const published = publicLocales(tenant);
  const languages: Record<string, string> = Object.fromEntries(
    published.map((locale) => [locale, origin + getPathname({ locale, href })]),
  );
  const fallback = published.find((l) => l === tenant?.defaultLocale) ?? published[0] ?? routing.defaultLocale;
  languages["x-default"] = origin + getPathname({ locale: fallback, href });
  return languages;
}

/** Canonical URL, hreflang alternates and Open Graph tags of one public page. */
export async function pageMetadata({ locale, href, title, description, tenant }: {
  locale: Locale;
  href: PublicHref;
  title?: string;
  description: string;
  tenant: TenantInfo | null;
}): Promise<Metadata> {
  const origin = await siteOrigin();
  const url = origin + getPathname({ locale, href });
  const siteName = tenant?.name ?? "Konnyaku";
  return {
    ...(title ? { title } : {}),
    description,
    alternates: { canonical: url, languages: alternates(origin, href, tenant) },
    openGraph: {
      type: "website",
      url,
      siteName,
      title: title ? `${title} · ${siteName}` : siteName,
      description,
      locale: locale === "vi" ? "vi_VN" : "en_US",
      alternateLocale: publicLocales(tenant).filter((l) => l !== locale).map((l) => (l === "vi" ? "vi_VN" : "en_US")),
    },
    twitter: { card: "summary", title: title ? `${title} · ${siteName}` : siteName, description },
  };
}
