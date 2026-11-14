import type { ReactNode } from "react";
import { NextIntlClientProvider } from "next-intl";
import { getLocale, getTranslations } from "next-intl/server";
import { appFont } from "@konnyaku/ui/kk/font";
import { Logo } from "@konnyaku/ui/kk/logo";
import { AppProviders } from "@konnyaku/ui/kk/providers";
import { TopBar, type NavLink } from "@konnyaku/ui/kk/shell";
import { requireMe, tenantInfo, type Realm } from "./index";

/**
 * Root document of a signed-in zone (learn, teach): top bar with the center brand, the zone name,
 * the user menu (sign out of this realm only) and the theme switch.
 */
export async function ZoneLayout({ realm, zone, links, children }: {
  realm: Realm;
  zone: string;
  links: NavLink[];
  children: ReactNode;
}) {
  const locale = await getLocale();
  const { me } = await requireMe(realm);
  const tenant = await tenantInfo();
  const shell = await getTranslations("shell");
  const roles = await getTranslations("roles");
  return (
    <html lang={locale} suppressHydrationWarning className={appFont.variable}>
      <body className="min-h-dvh bg-muted/30 font-sans">
        <AppProviders>
          <NextIntlClientProvider>
            <TopBar
              brand={<Logo name={tenant?.name ?? ""} suffix={zone} />}
              links={links}
              user={{ name: me.displayName, detail: me.roles.map((r) => (roles.has(r) ? roles(r) : r)).join(", ") }}
              logoutAction={`/api/session/${realm}/logout`}
              labels={{ theme: shell("theme"), menu: shell("menu"), logout: shell("logout"), close: shell("close") }}
            />
            <main className="mx-auto w-full max-w-6xl px-4 py-6 sm:px-6 sm:py-10">{children}</main>
          </NextIntlClientProvider>
        </AppProviders>
      </body>
    </html>
  );
}
