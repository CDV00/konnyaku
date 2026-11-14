import { NextResponse, type NextRequest } from "next/server";
import { apiFetch, sessionToken } from "@konnyaku/session";

export async function POST(request: NextRequest, { params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const token = await sessionToken("staff");
  if (!token) return NextResponse.json({ code: "unknown" }, { status: 401 });
  const response = await apiFetch(`/api/learning/sessions/${id}/attendance/confirm`, { method: "POST", token, body: await request.text() });
  if (response.ok) return new NextResponse(null, { status: 204 });
  const body = await response.json().catch(() => ({ code: "unknown" }));
  return NextResponse.json({ code: body.code ?? "unknown" }, { status: 400 });
}
