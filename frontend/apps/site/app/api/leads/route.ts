import { NextResponse, type NextRequest } from "next/server";
import { apiFetch } from "@konnyaku/session";

/** Relays the public registration form to the API with the tenant host and the visitor IP (rate limiting). */
export async function POST(request: NextRequest) {
  const forwardedFor = request.headers.get("x-forwarded-for") ?? "";
  const response = await apiFetch("/api/crm/public/leads", {
    method: "POST",
    body: await request.text(),
    headers: forwardedFor ? { "X-Forwarded-For": forwardedFor } : {},
  });
  if (response.ok) return new NextResponse(null, { status: 202 });
  const body = await response.json().catch(() => ({ code: "unknown" }));
  return NextResponse.json({ code: body.code ?? "unknown" }, { status: response.status === 429 ? 429 : 400 });
}
