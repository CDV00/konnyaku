import type { ReactNode } from "react";
import { NextIntlClientProvider } from "next-intl";
import { getLocale } from "next-intl/server";
import { appFont } from "@konnyaku/ui/kk/font";
import { AppProviders } from "@konnyaku/ui/kk/providers";
import "../../globals.css";

/** Root document of the console sign-in (admin host only): no tenant branding, no site navigation. */
export default async function PlatformLoginLayout({ children }: { children: ReactNode }) {
  return (
    <html lang={await getLocale()} suppressHydrationWarning className={appFont.variable}>
      <body className="min-h-dvh font-sans">
        <AppProviders>
          <NextIntlClientProvider>{children}</NextIntlClientProvider>
        </AppProviders>
      </body>
    </html>
  );
}
