import { getRequestConfig } from "next-intl/server";
import { hasLocale } from "next-intl";
import { cookies, headers } from "next/headers";
import { isLocale, loadMessages, localeCookie, matchLocale } from "@konnyaku/i18n";
import { tenantInfo } from "@konnyaku/session";
import { isAdminHost, platformTimeZone } from "../platform-host";
import { routing } from "./routing";

// Public pages: locale from the URL. Pages outside [locale] (login): cookie, then Accept-Language.
export default getRequestConfig(async ({ requestLocale }) => {
  const requested = await requestLocale;
  if (hasLocale(routing.locales, requested)) {
    return { locale: requested, timeZone: (await tenantInfo())?.timeZone ?? "UTC", messages: loadMessages(requested) };
  }
  const fromCookie = (await cookies()).get(localeCookie)?.value;
  const requestHeaders = await headers();
  const locale = isLocale(fromCookie) ? fromCookie : matchLocale(requestHeaders.get("accept-language"));
  // The platform console (admin host) belongs to no tenant.
  const timeZone = isAdminHost(requestHeaders.get("host")) ? platformTimeZone : ((await tenantInfo())?.timeZone ?? "UTC");
  return { locale, timeZone, messages: loadMessages(locale) };
});
