import { getRequestConfig } from "next-intl/server";
import { hasLocale } from "next-intl";
import { cookies, headers } from "next/headers";
import { isLocale, loadMessages, localeCookie, matchLocale } from "@konnyaku/i18n";
import { routing } from "./routing";

// Public pages: locale from the URL. Pages outside [locale] (login): cookie, then Accept-Language.
export default getRequestConfig(async ({ requestLocale }) => {
  const requested = await requestLocale;
  if (hasLocale(routing.locales, requested)) {
    return { locale: requested, messages: loadMessages(requested) };
  }
  const fromCookie = (await cookies()).get(localeCookie)?.value;
  const locale = isLocale(fromCookie) ? fromCookie : matchLocale((await headers()).get("accept-language"));
  return { locale, messages: loadMessages(locale) };
});
