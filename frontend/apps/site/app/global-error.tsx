"use client";

import { useEffect, useState } from "react";
import { RotateCcw, TriangleAlert } from "lucide-react";
import { appFont } from "@konnyaku/ui/kk/font";
import { AppProviders } from "@konnyaku/ui/kk/providers";
import en from "@konnyaku/i18n/messages/en/status.json";
import vi from "@konnyaku/i18n/messages/vi/status.json";
import { primaryAction, secondaryAction, StatusView } from "./status-view";
import "./globals.css";

/** Language without next-intl (the root layout, and its provider, failed): URL prefix, then cookie, then browser. */
function guessLocale(): "vi" | "en" {
  const fromPath = window.location.pathname.split("/")[1];
  const fromCookie = /(?:^|;\s*)NEXT_LOCALE=(vi|en)/.exec(document.cookie)?.[1];
  const candidate = fromPath === "vi" || fromPath === "en" ? fromPath : fromCookie ?? navigator.language.split("-")[0];
  return candidate === "en" ? "en" : "vi";
}

/** Last-resort error page: replaces the root layout when it throws (e.g. the API is unreachable). */
export default function GlobalError({ error, retry }: { error: Error & { digest?: string }; retry: () => void }) {
  const [locale, setLocale] = useState<"vi" | "en">("vi");
  useEffect(() => {
    console.error(error);
    setLocale(guessLocale()); // eslint-disable-line react-hooks/set-state-in-effect -- window is only readable after hydration
  }, [error]);
  const m = (locale === "en" ? en : vi).error;
  return (
    <html lang={locale} suppressHydrationWarning className={appFont.variable}>
      <body className="min-h-dvh font-sans">
        <title>{m.title}</title>
        <AppProviders>
          <main className="grid min-h-dvh place-items-center">
            <StatusView
              icon={TriangleAlert}
              code="500"
              title={m.title}
              body={m.body}
              footnote={error.digest ? m.reference.replace("{digest}", error.digest) : undefined}
            >
              <button type="button" onClick={() => retry()} className={primaryAction}>
                <RotateCcw className="size-4" aria-hidden />
                {m.retry}
              </button>
              <a href={`/${locale}`} className={secondaryAction}>{m.home}</a>
            </StatusView>
          </main>
        </AppProviders>
      </body>
    </html>
  );
}
