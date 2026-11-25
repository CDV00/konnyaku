import type { ReactNode } from "react";
import type { Metadata } from "next";
import { NextIntlClientProvider } from "next-intl";
import { getLocale, getTranslations } from "next-intl/server";
import { appFont } from "@konnyaku/ui/kk/font";
import { Logo } from "@konnyaku/ui/kk/logo";
import { AppProviders } from "@konnyaku/ui/kk/providers";
import { SidebarShell, type NavLink } from "@konnyaku/ui/kk/shell";
import { requireOperator } from "../lib";
import "../../globals.css";

export const metadata: Metadata = { robots: { index: false, follow: false } };

/** Platform console (admin host only, see proxy.ts): Konnyaku operators manage centers here. */
export default async function PlatformLayout({ children }: { children: ReactNode }) {
  const locale = await getLocale();
  const { me } = await requireOperator();
  const t = await getTranslations("platform");
  const shell = await getTranslations("shell");
  const links: NavLink[] = me.mustChangePassword
    ? [{ href: "/platform/account", label: t("nav.account"), icon: "account" }]
    : [
        { href: "/platform", label: t("nav.tenants"), icon: "tenants", exact: true },
        { href: "/platform/audit", label: t("nav.audit"), icon: "audit" },
        { href: "/platform/operators", label: t("nav.operators"), icon: "operators" },
        { href: "/platform/account", label: t("nav.account"), icon: "account" },
      ];
  return (
    <html lang={locale} suppressHydrationWarning className={appFont.variable}>
      <body className="min-h-dvh font-sans">
        <AppProviders>
          <NextIntlClientProvider>
            <SidebarShell
              brand={<Logo name={t("brand")} suffix={t("zone")} />}
              links={links}
              user={{ name: me.displayName, detail: `${t("role")} · ${me.email}` }}
              logoutAction="/platform/api/session/logout"
              labels={{ theme: shell("theme"), menu: shell("menu"), logout: shell("logout"), close: shell("close") }}
            >
              {children}
            </SidebarShell>
          </NextIntlClientProvider>
        </AppProviders>
      </body>
    </html>
  );
}
