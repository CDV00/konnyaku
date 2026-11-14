import type { NextConfig } from "next";
import createNextIntlPlugin from "next-intl/plugin";

const withNextIntl = createNextIntlPlugin("./i18n/request.ts");

const zones = {
  learn: process.env.LEARN_URL ?? "http://localhost:3001",
  teach: process.env.TEACH_URL ?? "http://localhost:3002",
  ops: process.env.OPS_URL ?? "http://localhost:3003",
};

// Root zone of the tenant host: public pages + login; /learn, /teach, /ops are separate Next.js apps (Multi-Zones).
const config: NextConfig = {
  transpilePackages: ["@konnyaku/i18n"],
  async rewrites() {
    return Object.entries(zones).flatMap(([zone, url]) => [
      { source: `/${zone}`, destination: `${url}/${zone}` },
      { source: `/${zone}/:path*`, destination: `${url}/${zone}/:path*` },
    ]);
  },
};

export default withNextIntl(config);
