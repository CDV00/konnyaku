import type { ReactNode } from "react";
import Link from "next/link";
import { NextIntlClientProvider } from "next-intl";
import { getTranslations } from "next-intl/server";
import { CirclePause } from "lucide-react";
import { tenantInfo } from "@konnyaku/session";
import { appFont } from "@konnyaku/ui/kk/font";
import { Logo } from "@konnyaku/ui/kk/logo";
import { AppProviders } from "@konnyaku/ui/kk/providers";
import { TopBar } from "@konnyaku/ui/kk/shell";
import { EmptyState } from "@konnyaku/ui/kk/blocks";
import { cn } from "@konnyaku/ui/lib/utils";
import "./globals.css";

/** Root document of the public site (landing, register, login, dev meeting room): one header, one footer. */
export async function SiteChrome({ locale, children }: { locale: string; children: ReactNode }) {
  const t = await getTranslations("siteNav");
  const shell = await getTranslations("shell");
  const platform = await getTranslations("platform.suspended");
  const tenant = await tenantInfo();
  const suspended = tenant?.status === "Suspended";
  const name = tenant?.name ?? t("brand");
  const other = locale === "vi" ? "en" : "vi";
  return (
    <html lang={locale} suppressHydrationWarning className={appFont.variable}>
      <body className={cn("min-h-dvh font-sans")}>
        <AppProviders>
          <NextIntlClientProvider>
            <div className="flex min-h-dvh flex-col">
              <TopBar
                brand={<a href={`/${locale}`}><Logo name={name} /></a>}
                links={[
                  { href: `/${locale}#programs`, label: t("programs"), icon: "book", external: true },
                  { href: `/${locale}/register`, label: t("register"), external: true },
                ]}
                labels={{ theme: shell("theme"), menu: shell("menu"), logout: shell("logout"), close: shell("close") }}
                actions={
                  <>
                    <a
                      href={`/${other}`}
                      className="rounded-md px-2 py-1 text-xs font-semibold tracking-wide text-muted-foreground uppercase hover:bg-accent hover:text-foreground"
                      hrefLang={other}
                      title={t("switchLocale")}
                    >
                      {other}
                    </a>
                    <Link
                      href="/login"
                      className="inline-flex h-9 items-center rounded-md bg-primary px-4 text-sm font-medium text-primary-foreground shadow-xs transition-colors hover:bg-primary/90"
                    >
                      {t("login")}
                    </Link>
                  </>
                }
              />
              <div className="flex-1">
                {suspended ? (
                  // A suspended center (platform console) shows only this notice: no catalogue, form or sign-in.
                  <div className="mx-auto flex max-w-xl px-4 py-24">
                    <EmptyState icon={CirclePause} title={platform("title")} description={platform("body", { name })} className="w-full" />
                  </div>
                ) : (
                  children
                )}
              </div>
              <footer className="border-t">
                <div className="mx-auto flex max-w-6xl flex-col gap-2 px-4 py-8 text-sm text-muted-foreground sm:flex-row sm:items-center sm:justify-between sm:px-6">
                  <Logo name={name} className="text-foreground" />
                  <p>{t("footer", { name })}</p>
                </div>
              </footer>
            </div>
          </NextIntlClientProvider>
        </AppProviders>
      </body>
    </html>
  );
}
