import { NextResponse } from "next/server";
import { sessionCookie } from "@konnyaku/session";

/**
 * Form-friendly sign out used by the signed-in areas (plain HTML form, no client script).
 * Relative Location: request.url can be a normalized origin (localhost) and would drop the tenant host.
 */
export async function POST(_: Request, { params }: { params: Promise<{ realm: string }> }) {
  const { realm } = await params;
  const result = new NextResponse(null, {
    status: 303,
    headers: { Location: `/login?realm=${realm === "staff" ? "staff" : "learn"}` },
  });
  if (realm === "learn" || realm === "staff") result.cookies.delete(sessionCookie[realm]);
  return result;
}
