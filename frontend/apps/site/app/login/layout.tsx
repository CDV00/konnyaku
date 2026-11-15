import type { ReactNode } from "react";
import { getLocale } from "next-intl/server";
import { SiteChrome } from "../site-chrome";

export default async function LoginLayout({ children }: { children: ReactNode }) {
  return <SiteChrome locale={await getLocale()}>{children}</SiteChrome>;
}
