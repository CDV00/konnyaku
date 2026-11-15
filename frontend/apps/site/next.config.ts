import path from "node:path";
import type { NextConfig } from "next";
import createNextIntlPlugin from "next-intl/plugin";

const withNextIntl = createNextIntlPlugin("./i18n/request.ts");

const config: NextConfig = {
  transpilePackages: ["@konnyaku/i18n", "@konnyaku/session", "@konnyaku/ui"],
  // Docker image (frontend/Dockerfile): a self-contained server.js. Traced from the workspace root so packages/* are included.
  ...(process.env.NEXT_STANDALONE === "1" && {
    output: "standalone",
    outputFileTracingRoot: path.join(import.meta.dirname, "../.."),
  }),
};

export default withNextIntl(config);
