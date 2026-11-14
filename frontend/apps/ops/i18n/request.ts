import { getRequestConfig } from "next-intl/server";
import { cookies, headers } from "next/headers";
import { isLocale, loadMessages, localeCookie, matchLocale } from "@konnyaku/i18n";
import { tenantInfo } from "@konnyaku/session";

// After sign-in the locale comes from the user profile (Phase 1); until then: cookie, then Accept-Language.
export default getRequestConfig(async () => {
  const fromCookie = (await cookies()).get(localeCookie)?.value;
  const locale = isLocale(fromCookie) ? fromCookie : matchLocale((await headers()).get("accept-language"));
  // Dates and times are shown in the center's time zone, whatever the server or browser runs in.
  const timeZone = (await tenantInfo())?.timeZone ?? "UTC";
  return { locale, timeZone, messages: loadMessages(locale) };
});
