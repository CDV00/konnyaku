import { NextResponse, type NextRequest } from "next/server";
import { apiFetch, sessionToken } from "@konnyaku/session";

const id = "[0-9a-f-]{36}";

/** The learner's own booking actions the browser may relay; the API still checks the booking belongs to the learner. */
const allowed = [
  `bookings/${id}/(postpone|cancel|mode)`,
  `makeups/${id}/(book|request)`,
  `sessions/${id}/(book|waitlist)`,
  `waitlist/${id}/(accept|withdraw)`,
].map((pattern) => new RegExp(`^${pattern}$`));

export async function POST(request: NextRequest, { params }: { params: Promise<{ path: string[] }> }) {
  const path = (await params).path.join("/");
  if (!allowed.some((pattern) => pattern.test(path))) return NextResponse.json({ code: "unknown" }, { status: 404 });
  const token = await sessionToken("learn");
  if (!token) return NextResponse.json({ code: "unknown" }, { status: 401 });
  const response = await apiFetch(`/api/scheduling/my/${path}`, { method: "POST", token, body: await request.text() });
  const text = await response.text();
  if (response.ok) return new NextResponse(text || "{}", { status: 200, headers: { "Content-Type": "application/json" } });
  let body: { code?: string; params?: Record<string, unknown> } = {};
  try {
    body = JSON.parse(text) as typeof body;
  } catch {
    // Empty or non-JSON error body.
  }
  return NextResponse.json({ code: body.code ?? "unknown", params: body.params ?? {} }, { status: 400 });
}
