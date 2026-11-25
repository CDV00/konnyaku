import type { Metadata } from "next";
import type { ReactNode } from "react";
import { getTranslations } from "next-intl/server";
import { ZoneLayout } from "@konnyaku/session/zone-layout";
import "../globals.css";

/** Signed-in and utility pages stay out of search results (robots.txt also disallows them). */
export const metadata: Metadata = { robots: { index: false, follow: false } };

export default async function RootLayout({ children }: { children: ReactNode }) {
  const t = await getTranslations("learn");
  return (
    <ZoneLayout realm="learn" zone={t("zone")} links={[
        { href: "/learn", label: t("nav.home"), icon: "home", exact: true },
        { href: "/learn/payments", label: t("nav.payments"), icon: "billing" },
      ]}>
      {children}
    </ZoneLayout>
  );
}
