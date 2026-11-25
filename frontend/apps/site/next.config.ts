import path from "node:path";
import type { NextConfig } from "next";
import createNextIntlPlugin from "next-intl/plugin";

const withNextIntl = createNextIntlPlugin("./i18n/request.ts");

/** Not for search engines: signed-in zones, sign-in, route handlers, meeting and classroom rooms (works for non-HTML too). */
const noindex = [{ key: "X-Robots-Tag", value: "noindex, nofollow" }];

const config: NextConfig = {
  transpilePackages: ["@konnyaku/i18n", "@konnyaku/session", "@konnyaku/ui"],
  async headers() {
    return ["learn", "teach", "ops", "login", "api", "dev-meeting", "room"].flatMap((segment) => [
      { source: `/${segment}`, headers: noindex },
      { source: `/${segment}/:path*`, headers: noindex },
    ]);
  },
  // Docker image (frontend/Dockerfile): a self-contained server.js. Traced from the workspace root so packages/* are included.
  ...(process.env.NEXT_STANDALONE === "1" && {
    output: "standalone",
    outputFileTracingRoot: path.join(import.meta.dirname, "../.."),
  }),
};

export default withNextIntl(config);
