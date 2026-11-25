import { NextResponse, type NextRequest } from "next/server";
import { apiFetch } from "@konnyaku/session";
import { qrDataUrl } from "../../qr";
import { errorFrom, landing, withSessionCookie, type SignIn } from "../../session/sign-in";

const paths = {
  verify: "/api/identity/staff/login/2fa",
  setup: "/api/identity/staff/login/2fa/setup",
  enable: "/api/identity/staff/login/2fa/enable",
} as const;

/**
 * Second step of a staff sign-in: `verify` (TOTP or recovery code), or for roles that must use 2FA `setup` (secret + QR)
 * then `enable` (first code; answers the recovery codes to show once). A finished step sets the session cookie.
 */
export async function POST(request: NextRequest, { params }: { params: Promise<{ step: string }> }) {
  const { step } = await params;
  if (step !== "verify" && step !== "setup" && step !== "enable") return NextResponse.json({ code: "unknown" }, { status: 404 });

  const response = await apiFetch(paths[step], { method: "POST", body: await request.text() });
  if (!response.ok) return errorFrom(response);
  const body = await response.json();
  if (step === "setup") {
    const setup = body as { secret: string; otpAuthUri: string };
    return NextResponse.json({ secret: setup.secret, qr: await qrDataUrl(setup.otpAuthUri) });
  }
  if (step === "enable") {
    const enrolled = body as { recoveryCodes: string[]; signIn: SignIn };
    return withSessionCookie(
      NextResponse.json({ recoveryCodes: enrolled.recoveryCodes, redirect: landing("staff", enrolled.signIn) }),
      "staff",
      enrolled.signIn,
    );
  }
  const signIn = body as SignIn;
  return withSessionCookie(NextResponse.json({ redirect: landing("staff", signIn) }), "staff", signIn);
}
