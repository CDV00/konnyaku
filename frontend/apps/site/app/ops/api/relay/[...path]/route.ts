import { NextResponse, type NextRequest } from "next/server";
import { apiFetch, sessionToken } from "@konnyaku/session";

const id = "[0-9a-f-]{36}";

/** Admin writes the ops screens may relay; anything else is not forwarded. The API still checks every permission. */
const allowed = [
  /^organization\/branches$/,
  /^identity\/staff$/,
  new RegExp(`^hr/teachers/${id}/rate$`),
  /^academic\/programs$/,
  new RegExp(`^academic/programs/${id}/levels$`),
  /^academic\/classes$/,
  new RegExp(`^scheduling/classes/${id}/schedule$`),
  new RegExp(`^crm/leads/${id}/status$`),
  /^billing\/packages$/,
  /^billing\/orders$/,
  new RegExp(`^billing/orders/${id}/payments$`),
  new RegExp(`^notification/messages/${id}/resend$`),
];

type Context = { params: Promise<{ path: string[] }> };

async function relay(request: NextRequest, { params }: Context, method: "POST" | "PUT") {
  const path = (await params).path.join("/");
  if (!allowed.some((pattern) => pattern.test(path))) return NextResponse.json({ code: "unknown" }, { status: 404 });
  const token = await sessionToken("staff");
  if (!token) return NextResponse.json({ code: "unauthorized" }, { status: 401 });
  const response = await apiFetch(`/api/${path}`, { method, token, body: await request.text() });
  const text = await response.text();
  if (response.ok) {
    return new NextResponse(text || "{}", { status: 200, headers: { "Content-Type": "application/json" } });
  }
  let code = response.status === 403 ? "forbidden" : "unknown";
  try {
    code = (JSON.parse(text) as { code?: string }).code ?? code;
  } catch {
    // Empty or non-JSON error body: keep the status-based code.
  }
  return NextResponse.json({ code }, { status: 400 });
}

export const POST = (request: NextRequest, context: Context) => relay(request, context, "POST");
export const PUT = (request: NextRequest, context: Context) => relay(request, context, "PUT");
