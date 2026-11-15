import { NextResponse, type NextRequest } from "next/server";
import createMiddleware from "next-intl/middleware";
import { routing } from "./i18n/routing";

const intl = createMiddleware(routing);

// Signed-in areas (learn, teach, ops), sign-in and route handlers take the locale from the cookie / user, not the URL.
const ownSegments = new Set(["learn", "teach", "ops", "login", "api", "dev-meeting"]);

export default function proxy(request: NextRequest) {
  const segment = request.nextUrl.pathname.split("/")[1] ?? "";
  if (ownSegments.has(segment)) return NextResponse.next();
  return intl(request);
}

export const config = {
  matcher: ["/((?!_next|_vercel|.*\\..*).*)"],
};
