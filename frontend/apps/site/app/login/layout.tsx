import type { Metadata } from "next";
import type { ReactNode } from "react";
import { getLocale } from "next-intl/server";
import { SiteChrome } from "../site-chrome";

/** Signed-in and utility pages stay out of search results (robots.txt also disallows them). */
export const metadata: Metadata = { robots: { index: false, follow: false } };

export default async function LoginLayout({ children }: { children: ReactNode }) {
  return <SiteChrome locale={await getLocale()}>{children}</SiteChrome>;
}
