import { NextResponse, type NextRequest } from "next/server";
import { apiFetch } from "@konnyaku/session";
import { operatorToken } from "../../../lib";

const id = "[0-9a-f-]{36}";

/** Console writes the platform screens may relay (below /api/platform/console/); the API checks everything again. */
const allowed = [
  /^tenants$/,
  new RegExp(`^tenants/${id}/(admins|suspend|resume|domains|domains/remove)$`),
  /^operators$/,
  new RegExp(`^operators/${id}/(disable|enable)$`),
  /^me\/password$/,
];

export async function POST(request: NextRequest, { params }: { params: Promise<{ path: string[] }> }) {
  const path = (await params).path.join("/");
  if (!allowed.some((pattern) => pattern.test(path))) return NextResponse.json({ code: "unknown" }, { status: 404 });
  const token = await operatorToken();
  if (!token) return NextResponse.json({ code: "unauthorized" }, { status: 401 });
  const forwardedFor = request.headers.get("x-forwarded-for");
  const response = await apiFetch(`/api/platform/console/${path}`, {
    method: "POST",
    token,
    body: await request.text(),
    headers: forwardedFor ? { "X-Forwarded-For": forwardedFor } : {},
  });
  const text = await response.text();
  if (response.ok) return new NextResponse(text || "{}", { status: 200, headers: { "Content-Type": "application/json" } });
  let code = response.status === 401 ? "unauthorized" : response.status === 403 ? "forbidden" : "unknown";
  let codeParams: Record<string, unknown> = {};
  try {
    const body = JSON.parse(text) as { code?: string; params?: Record<string, unknown> };
    code = body.code ?? code;
    codeParams = body.params ?? {};
  } catch {
    // Empty or non-JSON error body: keep the status-based code.
  }
  return NextResponse.json({ code, params: codeParams }, { status: 400 });
}
