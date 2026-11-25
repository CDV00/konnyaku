import { NextResponse } from "next/server";
import { sessionCookie, type Realm } from "@konnyaku/session";

/** A finished sign-in from the API (access token), or the second step a staff sign-in still needs. */
export type SignIn = {
  accessToken: string | null;
  expiresAt: string;
  roles: string[];
  mustChangePassword: boolean;
  challenge?: string | null;
  nextStep?: "verify" | "enroll" | null;
};

/** Where a finished sign-in lands: the change-password page first when the password is temporary. */
export function landing(realm: Realm, signIn: SignIn): string {
  if (realm === "learn") return signIn.mustChangePassword ? "/learn/change-password?required=1" : "/learn";
  const home = signIn.roles.every((role) => role === "Teacher") ? "/teach" : "/ops";
  return signIn.mustChangePassword ? `${home}/account?required=1` : home;
}

/** Stores the access token in the realm's httpOnly cookie (never readable by scripts). */
export function withSessionCookie<T>(response: NextResponse<T>, realm: Realm, signIn: SignIn): NextResponse<T> {
  response.cookies.set(sessionCookie[realm], signIn.accessToken ?? "", {
    httpOnly: true,
    sameSite: "lax",
    secure: process.env.NODE_ENV === "production",
    path: "/",
    expires: new Date(signIn.expiresAt),
  });
  return response;
}

/** API error body `{ code }` as the client forms expect it (status 400, 429 kept for rate limits). */
export async function errorFrom(response: Response) {
  const body = (await response.json().catch(() => ({}))) as { code?: string };
  const status = response.status === 429 ? 429 : 400;
  return NextResponse.json({ code: body.code ?? (status === 429 ? "rate_limited" : "unknown") }, { status });
}
