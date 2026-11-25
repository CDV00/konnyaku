import type { Metadata } from "next";
import type { ReactNode } from "react";
import { NextIntlClientProvider } from "next-intl";
import { getLocale, getTranslations } from "next-intl/server";
import { redirect } from "next/navigation";
import { requireMe, tenantInfo } from "@konnyaku/session";
import { InboxBell, InboxProvider } from "@konnyaku/session/inbox";
import { SessionWatch } from "@konnyaku/session/session-watch";
import { appFont } from "@konnyaku/ui/kk/font";
import { Logo } from "@konnyaku/ui/kk/logo";
import { AppProviders } from "@konnyaku/ui/kk/providers";
import { SidebarShell, type NavLink } from "@konnyaku/ui/kk/shell";
import { getAccess } from "./lib";
import "../globals.css";

const sections = ["leads", "students", "billing", "classes", "programs", "staff", "branches", "payroll"] as const;

/** Signed-in and utility pages stay out of search results (robots.txt also disallows them). */
export const metadata: Metadata = { robots: { index: false, follow: false } };

export default async function RootLayout({ children }: { children: ReactNode }) {
  const locale = await getLocale();
  const { me, token } = await requireMe("staff");
  // Teaching-only accounts have nothing to operate: their area is /teach (the sign-in sends them there too).
  if (me.roles.every((role) => role === "Teacher")) redirect("/teach");
  const access = await getAccess(token);
  const tenant = await tenantInfo();
  const t = await getTranslations("ops");
  const shell = await getTranslations("shell");
  const roles = await getTranslations("roles");
  const assessment = await getTranslations("assessment");
  const links: NavLink[] = [
    { href: "/ops", label: t("nav.home"), icon: "home", exact: true },
    ...sections.flatMap((section): NavLink[] => [
      { href: `/ops/${section}`, label: t(`nav.${section}`), icon: section },
      // Make-ups and leave sit under classes, for whoever schedules them.
      ...(section === "classes" && access.any("scheduling.schedules")
        ? [{ href: "/ops/makeups", label: t("nav.makeups"), icon: "makeups" as const }]
        : []),
    ]),
    // Data subject requests and the personal data access log: the tenant admin only.
    ...(access.can("crm.privacy") ? [{ href: "/ops/privacy", label: t("nav.privacy"), icon: "privacy" as const }] : []),
    ...(access.can("identity.accounts") ? [{ href: "/ops/security", label: t("nav.security"), icon: "security" as const }] : []),
    ...(access.any("notification.messages")
      ? [{ href: "/ops/notifications", label: t("nav.notifications"), icon: "notifications" as const }]
      : []),
    ...(access.any("assessment.manage") ? [{ href: "/ops/assessments", label: assessment("ops.nav"), icon: "tests" as const }] : []),
    // Every file of the center, storage used and quota: center-wide administrators.
    ...(access.can("content.files") ? [{ href: "/ops/files", label: t("nav.files"), icon: "files" as const }] : []),
  ];
  return (
    <html lang={locale} suppressHydrationWarning className={appFont.variable}>
      <body className="min-h-dvh font-sans">
        <AppProviders>
          <NextIntlClientProvider>
            <InboxProvider realm="staff">
              <SidebarShell
                brand={<Logo name={tenant?.name ?? ""} suffix={t("zone")} />}
                links={links}
                user={{ name: me.displayName, detail: me.roles.map((r) => (roles.has(r) ? roles(r) : r)).join(", ") }}
                logoutAction="/api/session/staff/logout"
                accountHref="/ops/account"
                labels={{ theme: shell("theme"), menu: shell("menu"), logout: shell("logout"), close: shell("close"), account: shell("account") }}
                actions={<InboxBell href="/ops/inbox" settingsHref="/ops/inbox/settings" />}
              >
                <SessionWatch realm="staff" />
                {children}
              </SidebarShell>
            </InboxProvider>
          </NextIntlClientProvider>
        </AppProviders>
      </body>
    </html>
  );
}
