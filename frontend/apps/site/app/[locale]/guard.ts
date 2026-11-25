import { notFound } from "next/navigation";
import { hasLocale } from "next-intl";
import { setRequestLocale } from "next-intl/server";
import type { Locale } from "@konnyaku/i18n";
import { tenantInfo } from "@konnyaku/session";
import { routing } from "@/i18n/routing";

/**
 * Entry check of every public page: an unknown language segment (/wp-login.php lands here too) or a host that belongs
 * to no center ends in [locale]/not-found, rendered inside the site chrome with a 404 status. The check lives in the
 * pages, not the layout: notFound() thrown by a root layout would skip our not-found page.
 */
export async function publicLocale(params: Promise<{ locale: string }>): Promise<Locale> {
  const { locale } = await params;
  if (!hasLocale(routing.locales, locale) || !(await tenantInfo())) notFound();
  setRequestLocale(locale);
  return locale;
}
