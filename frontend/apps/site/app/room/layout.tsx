import type { ReactNode } from "react";
import { NextIntlClientProvider } from "next-intl";
import { getLocale } from "next-intl/server";
import { appFont } from "@konnyaku/ui/kk/font";
import { AppProviders } from "@konnyaku/ui/kk/providers";
import "../globals.css";

/** Root document of the built-in classroom: full screen, no site chrome. */
export default async function RoomLayout({ children }: { children: ReactNode }) {
  return (
    <html lang={await getLocale()} suppressHydrationWarning className={appFont.variable}>
      <body className="h-dvh overflow-hidden bg-background font-sans">
        <AppProviders>
          <NextIntlClientProvider>{children}</NextIntlClientProvider>
        </AppProviders>
      </body>
    </html>
  );
}
