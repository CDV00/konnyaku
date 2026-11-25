import type { Metadata } from "next";
import type { ReactNode } from "react";
import { NextIntlClientProvider } from "next-intl";
import { getLocale, getTranslations } from "next-intl/server";
import { requireMe, tenantInfo } from "@konnyaku/session";
import { appFont } from "@konnyaku/ui/kk/font";
import { Logo } from "@konnyaku/ui/kk/logo";
import { AppProviders } from "@konnyaku/ui/kk/providers";
import { SidebarShell, type NavLink } from "@konnyaku/ui/kk/shell";
import "../globals.css";

const sections = ["leads", "students", "billing", "classes", "programs", "staff", "branches", "payroll"] as const;

/** Signed-in and utility pages stay out of search results (robots.txt also disallows them). */
export const metadata: Metadata = { robots: { index: false, follow: false } };

export default async function RootLayout({ children }: { children: ReactNode }) {
  const locale = await getLocale();
  const { me } = await requireMe("staff");
  const tenant = await tenantInfo();
  const t = await getTranslations("ops");
  const shell = await getTranslations("shell");
  const roles = await getTranslations("roles");
  const links: NavLink[] = [
    { href: "/ops", label: t("nav.home"), icon: "home", exact: true },
    ...sections.map((section) => ({ href: `/ops/${section}`, label: t(`nav.${section}`), icon: section })),
    ...(me.roles.includes("TenantAdmin")
      ? [{ href: "/ops/notifications", label: t("nav.notifications"), icon: "notifications" as const }]
      : []),
  ];
  return (
    <html lang={locale} suppressHydrationWarning className={appFont.variable}>
      <body className="min-h-dvh font-sans">
        <AppProviders>
          <NextIntlClientProvider>
            <SidebarShell
              brand={<Logo name={tenant?.name ?? ""} suffix={t("zone")} />}
              links={links}
              user={{ name: me.displayName, detail: me.roles.map((r) => (roles.has(r) ? roles(r) : r)).join(", ") }}
              logoutAction="/api/session/staff/logout"
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
