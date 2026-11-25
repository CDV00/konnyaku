import type { MetadataRoute } from "next";
import { tenantInfo } from "@konnyaku/session";
import { getPathname } from "@/i18n/navigation";
import { publicPages } from "@/i18n/routing";
import { alternates, publicLocales, siteOrigin } from "./seo";

/** Public pages of the center on this host, one entry per published language with hreflang alternates. */
export default async function sitemap(): Promise<MetadataRoute.Sitemap> {
  const tenant = await tenantInfo();
  // Unknown host, platform console host (no tenant) or suspended center: nothing to list.
  if (!tenant || tenant.status === "Suspended") return [];
  const origin = await siteOrigin();
  return publicLocales(tenant).flatMap((locale) =>
    publicPages.map((page) => ({
      url: origin + getPathname({ locale, href: page.href }),
      changeFrequency: page.changeFrequency,
      priority: page.priority,
      alternates: { languages: alternates(origin, page.href, tenant) },
    })),
  );
}
