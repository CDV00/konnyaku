import { NextResponse, type NextRequest } from "next/server";
import { apiFetch, sessionToken, type Realm } from "@konnyaku/session";

/**
 * A fresh ticket for the signaling service, asked by the room page when it connects and on every reconnect.
 * The API checks again that this person may attend the session (seat in the class, its teacher, a coordinator).
 */
export async function POST(request: NextRequest) {
  const { code, realm } = (await request.json().catch(() => ({}))) as { code?: string; realm?: Realm };
  if (typeof code !== "string" || (realm !== "learn" && realm !== "staff")) {
    return NextResponse.json({ code: "unknown" }, { status: 400 });
  }
  const token = await sessionToken(realm);
  if (!token) return NextResponse.json({ code: "session_expired" }, { status: 401 });
  const response = await apiFetch(`/api/workspace/rooms/${encodeURIComponent(code)}/ticket`, { method: "POST", token });
  if (response.ok) return NextResponse.json(await response.json(), { headers: { "Cache-Control": "no-store" } });
  const body = (await response.json().catch(() => ({}))) as { code?: string };
  const status = response.status === 401 ? 401 : response.status >= 500 ? 502 : 400;
  return NextResponse.json({ code: body.code ?? (response.status === 401 ? "session_expired" : "unknown") }, { status });
}
