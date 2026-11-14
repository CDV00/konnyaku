import type { NextConfig } from "next";
import createNextIntlPlugin from "next-intl/plugin";

const withNextIntl = createNextIntlPlugin("./i18n/request.ts");

// Served under the tenant host at /learn through Next.js Multi-Zones (the site app rewrites to it).
const config: NextConfig = {
  basePath: "/learn",
  transpilePackages: ["@konnyaku/i18n"],
};

export default withNextIntl(config);
