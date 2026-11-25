import { NextResponse, type NextRequest } from "next/server";
import { apiFetch, sessionCookie, sessionToken } from "@konnyaku/session";
import { errorFrom, landing, withSessionCookie, type SignIn } from "../sign-in";

const loginPath = { learn: "/api/identity/learn/login", staff: "/api/identity/staff/login" } as const;

/**
 * Signs in on one realm. A staff account with 2FA (or a role that must enroll it) gets a challenge instead of a
 * session: the login form continues with /api/mfa/*.
 */
export async function POST(request: NextRequest, { params }: { params: Promise<{ realm: string }> }) {
  const { realm } = await params;
  if (realm !== "learn" && realm !== "staff") return NextResponse.json({ code: "unknown" }, { status: 404 });

  const response = await apiFetch(loginPath[realm], { method: "POST", body: await request.text() });
  if (!response.ok) return errorFrom(response);
  const signIn = (await response.json()) as SignIn;
  if (!signIn.accessToken) return NextResponse.json({ challenge: signIn.challenge, nextStep: signIn.nextStep });
  return withSessionCookie(NextResponse.json({ redirect: landing(realm, signIn) }), realm, signIn);
}

/** Is this realm's session still active? 401 (and the cookie cleared) once it was revoked or expired. */
export async function GET(_: NextRequest, { params }: { params: Promise<{ realm: string }> }) {
  const { realm } = await params;
  if (realm !== "learn" && realm !== "staff") return NextResponse.json({ code: "unknown" }, { status: 404 });
  const token = await sessionToken(realm);
  const response = token ? await apiFetch("/api/identity/me", { token }) : null;
  if (response?.ok) return NextResponse.json({ active: true });
  if (response && response.status !== 401) return NextResponse.json({ active: true, unchecked: true });
  const result = NextResponse.json({ active: false }, { status: 401 });
  result.cookies.delete(sessionCookie[realm]);
  return result;
}

/** Sign out of one realm only (the API revokes the session); the other realm's session is untouched. */
export async function DELETE(_: NextRequest, { params }: { params: Promise<{ realm: string }> }) {
  const { realm } = await params;
  const result = NextResponse.json({ redirect: "/login" });
  if (realm === "learn" || realm === "staff") {
    const token = await sessionToken(realm);
    if (token) await apiFetch("/api/identity/logout", { method: "POST", token }).catch(() => null);
    result.cookies.delete(sessionCookie[realm]);
  }
  return result;
}
