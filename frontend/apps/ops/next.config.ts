import type { NextConfig } from "next";
import createNextIntlPlugin from "next-intl/plugin";

const withNextIntl = createNextIntlPlugin("./i18n/request.ts");

// Served under the tenant host at /ops through Next.js Multi-Zones (the site app rewrites to it).
const config: NextConfig = {
  basePath: "/ops",
  transpilePackages: ["@konnyaku/i18n", "@konnyaku/session", "@konnyaku/ui"],
};

export default withNextIntl(config);
