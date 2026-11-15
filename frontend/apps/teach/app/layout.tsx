import type { ReactNode } from "react";
import { getTranslations } from "next-intl/server";
import { ZoneLayout } from "@konnyaku/session/zone-layout";
import "./globals.css";

export default async function RootLayout({ children }: { children: ReactNode }) {
  const t = await getTranslations("teach");
  return (
    <ZoneLayout realm="staff" zone={t("zone")} links={[{ href: "/", label: t("nav.home"), icon: "home" }]}>
      {children}
    </ZoneLayout>
  );
}
