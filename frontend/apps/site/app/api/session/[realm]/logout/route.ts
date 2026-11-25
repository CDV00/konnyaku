import { NextResponse } from "next/server";
import { apiFetch, sessionCookie, sessionToken } from "@konnyaku/session";
import { forgetPushSubscription, pushCookie } from "../../../inbox/push";

/**
 * Form-friendly sign out used by the signed-in areas (plain HTML form, no client script): the push subscription of this
 * browser is deleted, the API revokes the session, then the cookies go. Relative Location: request.url can be a normalized origin (localhost) and would drop the tenant host.
 */
export async function POST(_: Request, { params }: { params: Promise<{ realm: string }> }) {
  const { realm } = await params;
  const result = new NextResponse(null, {
    status: 303,
    headers: { Location: `/login?realm=${realm === "staff" ? "staff" : "learn"}` },
  });
  if (realm === "learn" || realm === "staff") {
    const token = await sessionToken(realm);
    if (token) {
      await forgetPushSubscription(realm, token);
      await apiFetch("/api/identity/logout", { method: "POST", token }).catch(() => null);
    }
    result.cookies.delete(sessionCookie[realm]);
    result.cookies.delete(pushCookie[realm]);
  }
  return result;
}
