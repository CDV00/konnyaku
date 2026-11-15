import { NextResponse, type NextRequest } from "next/server";
import { apiFetch, sessionToken } from "@konnyaku/session";

export async function POST(_: NextRequest, { params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const token = await sessionToken("staff");
  if (!token) return NextResponse.json({ code: "unknown" }, { status: 401 });
  const response = await apiFetch(`/api/hr/payroll-periods/${id}/lock`, { method: "POST", token });
  if (response.ok) return new NextResponse(null, { status: 204 });
  const data = await response.json().catch(() => ({ code: "unknown" }));
  return NextResponse.json({ code: data.code ?? "unknown" }, { status: 400 });
}
