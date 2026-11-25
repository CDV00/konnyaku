import type { ReactNode } from "react";
import { getTranslations } from "next-intl/server";
import { ZoneLayout } from "@konnyaku/session/zone-layout";
import "../globals.css";

export default async function RootLayout({ children }: { children: ReactNode }) {
  const t = await getTranslations("teach");
  return (
    <ZoneLayout realm="staff" zone={t("zone")} accountHref="/teach/account" links={[{ href: "/teach", label: t("nav.home"), icon: "home", exact: true }]}>
      {children}
    </ZoneLayout>
  );
}
