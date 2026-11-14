import { NextResponse, type NextRequest } from "next/server";
import { apiFetch, sessionCookie, type Realm } from "@konnyaku/session";

type SignIn = { accessToken: string; expiresAt: string; roles: string[]; mustChangePassword: boolean };

const loginPath: Record<Realm, string> = { learn: "/api/identity/learn/login", staff: "/api/identity/staff/login" };

function landing(realm: Realm, signIn: SignIn): string {
  if (realm === "learn") return signIn.mustChangePassword ? "/learn/change-password" : "/learn";
  const onlyTeacher = signIn.roles.every((role) => role === "Teacher");
  return onlyTeacher ? "/teach" : "/ops";
}

/** Signs in on one realm and stores the token in that realm's httpOnly cookie (never readable by scripts). */
export async function POST(request: NextRequest, { params }: { params: Promise<{ realm: string }> }) {
  const { realm } = await params;
  if (realm !== "learn" && realm !== "staff") return NextResponse.json({ code: "unknown" }, { status: 404 });

  const response = await apiFetch(loginPath[realm], { method: "POST", body: await request.text() });
  const body = await response.json().catch(() => ({ code: "unknown" }));
  if (!response.ok) return NextResponse.json({ code: body.code ?? "unknown" }, { status: 400 });

  const signIn = body as SignIn;
  const result = NextResponse.json({ redirect: landing(realm, signIn) });
  result.cookies.set(sessionCookie[realm], signIn.accessToken, {
    httpOnly: true,
    sameSite: "lax",
    secure: process.env.NODE_ENV === "production",
    path: "/",
    expires: new Date(signIn.expiresAt),
  });
  return result;
}

/** Sign out of one realm only; the other realm's session is untouched. */
export async function DELETE(_: NextRequest, { params }: { params: Promise<{ realm: string }> }) {
  const { realm } = await params;
  const result = NextResponse.json({ redirect: "/login" });
  if (realm === "learn" || realm === "staff") result.cookies.delete(sessionCookie[realm]);
  return result;
}
