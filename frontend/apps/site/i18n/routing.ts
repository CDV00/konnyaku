import { defineRouting } from "next-intl/routing";
import { defaultLocale, locales } from "@konnyaku/i18n";

// Public pages keep the locale in the URL (SEO, shareable links); paths are translated per locale.
export const routing = defineRouting({
  locales,
  defaultLocale,
  localePrefix: "always",
  pathnames: {
    "/": "/",
    "/register": { vi: "/dang-ky", en: "/register" },
    "/privacy": { vi: "/chinh-sach-quyen-rieng-tu", en: "/privacy" },
    "/terms": { vi: "/dieu-khoan-su-dung", en: "/terms" },
  },
});

/** Public pages listed in the sitemap, with their change frequency and priority. */
export const publicPages = [
  { href: "/", changeFrequency: "weekly", priority: 1 },
  { href: "/register", changeFrequency: "monthly", priority: 0.8 },
  { href: "/privacy", changeFrequency: "yearly", priority: 0.3 },
  { href: "/terms", changeFrequency: "yearly", priority: 0.3 },
] as const;

export type PublicHref = (typeof publicPages)[number]["href"];
