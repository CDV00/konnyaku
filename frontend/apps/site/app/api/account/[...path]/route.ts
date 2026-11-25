import { NextResponse, type NextRequest } from "next/server";
import { apiFetch, sessionToken } from "@konnyaku/session";
import { qrDataUrl } from "../../qr";
import { errorFrom } from "../../session/sign-in";

const session = "[0-9a-f-]{36}";

/** The staff member's own account actions (ops and teach account pages); anything else is not forwarded. */
const allowed = [
  /^identity\/staff\/change-password$/,
  /^identity\/me\/2fa\/(setup|enable|disable|recovery-codes)$/,
  new RegExp(`^identity/me/sessions/${session}/revoke$`),
];

export async function POST(request: NextRequest, { params }: { params: Promise<{ path: string[] }> }) {
  const path = (await params).path.join("/");
  if (!allowed.some((pattern) => pattern.test(path))) return NextResponse.json({ code: "unknown" }, { status: 404 });
  const token = await sessionToken("staff");
  if (!token) return NextResponse.json({ code: "unauthorized" }, { status: 401 });
  const response = await apiFetch(`/api/${path}`, { method: "POST", token, body: await request.text() });
  if (!response.ok) return errorFrom(response);
  const text = await response.text();
  const body = (text ? JSON.parse(text) : {}) as Record<string, unknown>;
  if (path === "identity/me/2fa/setup") {
    const setup = body as { secret: string; otpAuthUri: string };
    return NextResponse.json({ secret: setup.secret, qr: await qrDataUrl(setup.otpAuthUri) });
  }
  return NextResponse.json(body);
}
