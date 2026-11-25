import type { MetadataRoute } from "next";
import { tenantInfo } from "@konnyaku/session";
import { privatePaths, siteOrigin } from "./seo";

/**
 * Per host: a center's site is crawlable except signed-in areas; an unknown host, the platform console host
 * (no tenant) and a suspended center are closed to crawlers.
 */
export default async function robots(): Promise<MetadataRoute.Robots> {
  const tenant = await tenantInfo();
  if (!tenant || tenant.status === "Suspended") return { rules: { userAgent: "*", disallow: "/" } };
  const origin = await siteOrigin();
  return {
    rules: { userAgent: "*", allow: "/", disallow: [...privatePaths] },
    sitemap: `${origin}/sitemap.xml`,
  };
}
