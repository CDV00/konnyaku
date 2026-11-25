import { NextResponse } from "next/server";
import { apiFetch } from "@konnyaku/session";

/** Relays a proof-of-work challenge for the tenant of this host; 204 when the captcha is switched off. */
export async function GET() {
  const response = await apiFetch("/api/crm/public/captcha");
  if (response.status === 204) return new NextResponse(null, { status: 204, headers: { "Cache-Control": "no-store" } });
  if (!response.ok) return NextResponse.json({ code: "unknown" }, { status: 502 });
  return NextResponse.json(await response.json(), { headers: { "Cache-Control": "no-store" } });
}
