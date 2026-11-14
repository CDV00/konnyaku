import type { ReactNode } from "react";
import Link from "next/link";
import { NextIntlClientProvider } from "next-intl";
import { getLocale, getTranslations } from "next-intl/server";
import "./ops.css";

const sections = ["leads", "students", "billing", "classes", "programs", "staff", "branches", "payroll"] as const;

export default async function RootLayout({ children }: { children: ReactNode }) {
  const locale = await getLocale();
  const t = await getTranslations("ops");
  return (
    <html lang={locale}>
      <body>
        <nav className="ops-nav">
          <Link href="/">{t("title")}</Link>
          {sections.map((section) => (
            <Link key={section} href={`/${section}`}>{t(`nav.${section}`)}</Link>
          ))}
        </nav>
        <NextIntlClientProvider>{children}</NextIntlClientProvider>
      </body>
    </html>
  );
}
