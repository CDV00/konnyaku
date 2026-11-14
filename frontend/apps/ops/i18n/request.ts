import { getRequestConfig } from "next-intl/server";
import { cookies, headers } from "next/headers";
import { isLocale, loadMessages, localeCookie, matchLocale } from "@konnyaku/i18n";

// After sign-in the locale comes from the user profile (Phase 1); until then: cookie, then Accept-Language.
export default getRequestConfig(async () => {
  const fromCookie = (await cookies()).get(localeCookie)?.value;
  const locale = isLocale(fromCookie) ? fromCookie : matchLocale((await headers()).get("accept-language"));
  return { locale, messages: loadMessages(locale) };
});
