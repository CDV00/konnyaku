import { NextResponse, type NextRequest } from "next/server";
import QRCode from "qrcode";
import { apiFetch } from "@konnyaku/session";
import { challengeCookie, operatorCookie } from "../../../lib";

type PasswordStep = {
  challenge: string;
  expiresAt: string;
  enrollmentRequired: boolean;
  secret: string | null;
  otpAuthUri: string | null;
};

type OperatorSession = {
  accessToken: string;
  expiresAt: string;
  mustChangePassword: boolean;
  recoveryCodes: string[] | null;
};

const cookieOptions = {
  httpOnly: true,
  sameSite: "strict" as const,
  secure: process.env.NODE_ENV === "production",
  path: "/platform",
};

/** Visitor IP for the API's rate limit and the audit log. */
function forwarded(request: NextRequest): HeadersInit {
  const value = request.headers.get("x-forwarded-for");
  return value ? { "X-Forwarded-For": value } : {};
}

async function errorFrom(response: Response): Promise<NextResponse> {
  const body = (await response.json().catch(() => ({}))) as { code?: string };
  const code = response.status === 429 ? "rate_limited" : (body.code ?? "unknown");
  return NextResponse.json({ code }, { status: 400 });
}

/**
 * Two-step operator sign-in, then sign-out:
 * - `login`: email + password → the challenge goes into a short httpOnly cookie; when TOTP is not set up yet the
 *   response carries the secret and a QR code (SVG) to scan;
 * - `verify`: TOTP or recovery code + challenge cookie → the access token goes into the platform cookie;
 * - `logout`: plain form post from the console menu.
 */
export async function POST(request: NextRequest, { params }: { params: Promise<{ step: string }> }) {
  const { step } = await params;

  if (step === "login") {
    const response = await apiFetch("/api/platform/auth/login", { method: "POST", body: await request.text(), headers: forwarded(request) });
    if (!response.ok) return errorFrom(response);
    const accepted = (await response.json()) as PasswordStep;
    const qr = accepted.otpAuthUri ? await QRCode.toString(accepted.otpAuthUri, { type: "svg", margin: 1, errorCorrectionLevel: "M" }) : null;
    const result = NextResponse.json({ enrollmentRequired: accepted.enrollmentRequired, secret: accepted.secret, otpAuthUri: accepted.otpAuthUri, qr });
    result.cookies.set(challengeCookie, accepted.challenge, { ...cookieOptions, expires: new Date(accepted.expiresAt) });
    return result;
  }

  if (step === "verify") {
    const challenge = request.cookies.get(challengeCookie)?.value;
    if (!challenge) return NextResponse.json({ code: "platform.challenge_expired" }, { status: 400 });
    const { code, recoveryCode } = (await request.json().catch(() => ({}))) as { code?: string; recoveryCode?: string };
    const response = await apiFetch("/api/platform/auth/verify", {
      method: "POST",
      body: JSON.stringify({ challenge, code, recoveryCode }),
      headers: forwarded(request),
    });
    if (!response.ok) return errorFrom(response);
    const session = (await response.json()) as OperatorSession;
    const result = NextResponse.json({
      redirect: session.mustChangePassword ? "/platform/account" : "/platform",
      recoveryCodes: session.recoveryCodes,
    });
    result.cookies.delete({ name: challengeCookie, path: "/platform" });
    result.cookies.set(operatorCookie, session.accessToken, { ...cookieOptions, expires: new Date(session.expiresAt) });
    return result;
  }

  if (step === "logout") {
    // Relative Location: request.url can be a normalized origin and would drop the admin host.
    const result = new NextResponse(null, { status: 303, headers: { Location: "/platform/login" } });
    result.cookies.delete({ name: operatorCookie, path: "/platform" });
    return result;
  }

  return NextResponse.json({ code: "unknown" }, { status: 404 });
}
