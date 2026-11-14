import { NextResponse, type NextRequest } from "next/server";
import createMiddleware from "next-intl/middleware";
import { routing } from "./i18n/routing";

const intl = createMiddleware(routing);

const zones: Record<string, string> = {
  learn: process.env.LEARN_URL ?? "http://localhost:3001",
  teach: process.env.TEACH_URL ?? "http://localhost:3002",
  ops: process.env.OPS_URL ?? "http://localhost:3003",
};

// Root zone of the tenant host. /learn, /teach and /ops are separate Next.js apps (Multi-Zones): the request is
// proxied to them with the original tenant host, which they forward to the API to resolve the tenant.
export default function proxy(request: NextRequest) {
  const zone = request.nextUrl.pathname.split("/")[1] ?? "";
  const target = zones[zone];
  if (target) {
    const url = new URL(request.nextUrl.pathname + request.nextUrl.search, target);
    const requestHeaders = new Headers(request.headers);
    requestHeaders.set("x-tenant-host", (request.headers.get("host") ?? "").split(":")[0] ?? "");
    requestHeaders.set("x-site-origin", request.nextUrl.origin);
    return NextResponse.rewrite(url, { request: { headers: requestHeaders } });
  }
  if (request.nextUrl.pathname.startsWith("/login") || request.nextUrl.pathname.startsWith("/api")) {
    return NextResponse.next();
  }
  return intl(request);
}

export const config = {
  // Zone paths are always proxied, including their /_next assets (which contain dots).
  matcher: ["/learn", "/learn/:path*", "/teach", "/teach/:path*", "/ops", "/ops/:path*", "/((?!_next|_vercel|.*\\..*).*)"],
};
