import type { ReactNode } from "react";
import { getTranslations } from "next-intl/server";
import { ZoneLayout } from "@konnyaku/session/zone-layout";
import "../globals.css";

export default async function RootLayout({ children }: { children: ReactNode }) {
  const t = await getTranslations("learn");
  return (
    <ZoneLayout realm="learn" zone={t("zone")} links={[{ href: "/learn", label: t("nav.home"), icon: "home", exact: true }]}>
      {children}
    </ZoneLayout>
  );
}
