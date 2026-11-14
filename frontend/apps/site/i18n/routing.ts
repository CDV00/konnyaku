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
  },
});
