import { NextResponse, type NextRequest } from "next/server";
import createMiddleware from "next-intl/middleware";
import { routing } from "./i18n/routing";
import { isAdminHost } from "./platform-host";

const intl = createMiddleware(routing);

// Signed-in areas (learn, teach, ops), sign-in and route handlers take the locale from the cookie / user, not the URL.
const ownSegments = new Set(["learn", "teach", "ops", "login", "api", "dev-meeting", "room"]);

/**
 * The platform console lives only on the admin host, and the admin host serves only the console:
 * /platform on a tenant host is a 404, and anything but /platform (and the health check) on the admin host
 * goes to the console. The API refuses console calls from other hosts too.
 */
export default function proxy(request: NextRequest) {
  const segment = request.nextUrl.pathname.split("/")[1] ?? "";
  if (isAdminHost(request.headers.get("host"))) {
    if (segment === "platform" || request.nextUrl.pathname === "/api/health") return NextResponse.next();
    // Built from the Host header: request.url may carry a normalized dev origin instead of the admin host.
    return NextResponse.redirect(new URL("/platform", `${request.nextUrl.protocol}//${request.headers.get("host")}`));
  }
  if (segment === "platform") return new NextResponse(null, { status: 404 });
  if (ownSegments.has(segment)) return NextResponse.next();
  return intl(request);
}

export const config = {
  matcher: ["/((?!_next|_vercel|.*\\..*).*)"],
};
