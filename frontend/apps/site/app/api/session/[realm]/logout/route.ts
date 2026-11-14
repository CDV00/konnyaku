import { NextResponse, type NextRequest } from "next/server";
import { sessionCookie } from "@konnyaku/session";

/** Form-friendly sign out used by the zone apps (plain HTML form, no client script). */
export async function POST(request: NextRequest, { params }: { params: Promise<{ realm: string }> }) {
  const { realm } = await params;
  const result = NextResponse.redirect(new URL(`/login?realm=${realm === "staff" ? "staff" : "learn"}`, request.url), 303);
  if (realm === "learn" || realm === "staff") result.cookies.delete(sessionCookie[realm]);
  return result;
}
