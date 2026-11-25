import { NextResponse, type NextRequest } from "next/server";
import { apiFetch, sessionToken } from "@konnyaku/session";

const id = "[0-9a-f-]{36}";
const route = (pattern: string) => new RegExp(`^${pattern.replaceAll("{id}", id)}$`);

/** Admin writes the ops screens may relay; anything else is not forwarded. The API still checks every permission. */
const writes: Record<"POST" | "PUT" | "DELETE", RegExp[]> = {
  POST: [
    route("organization/branches"),
    route("organization/branches/{id}/(deactivate|activate)"),
    route("organization/branches/{id}/rooms"),
    route("organization/rooms/{id}/(deactivate|activate)"),
    route("organization/holidays"),
    route("identity/staff"),
    route("identity/staff/{id}/(deactivate|activate)"),
    route("academic/programs"),
    route("academic/programs/{id}/(deactivate|activate)"),
    route("academic/programs/{id}/levels"),
    route("academic/levels/{id}/(deactivate|activate)"),
    route("academic/classes"),
    route("academic/classes/{id}/(close|reopen)"),
    route("scheduling/classes/{id}/schedule"),
    route("crm/leads/{id}/status"),
    route("crm/students/{id}/(deactivate|activate)"),
    route("billing/packages"),
    route("billing/packages/{id}/(deactivate|activate)"),
    route("billing/orders"),
    route("billing/orders/{id}/payments"),
    route("billing/orders/{id}/cancel"),
    route("billing/payments/{id}/void"),
    route("billing/orders/{id}/refunds"),
    route("billing/accounts/{id}/adjustments"),
    route("notification/messages/{id}/resend"),
  ],
  PUT: [
    route("organization/branches/{id}"),
    route("organization/rooms/{id}"),
    route("organization/holidays/{id}"),
    route("identity/staff/{id}"),
    route("hr/teachers/{id}/rate"),
    route("hr/rates/{id}"),
    route("academic/programs/{id}"),
    route("academic/levels/{id}"),
    route("academic/classes/{id}"),
    route("scheduling/classes/{id}/schedule"),
    route("crm/leads/{id}"),
    route("crm/students/{id}"),
    route("billing/packages/{id}"),
    route("billing/orders/{id}"),
    route("billing/payments/{id}"),
  ],
  DELETE: [route("organization/holidays/{id}"), route("hr/rates/{id}")],
};

/** Reads made from the browser (change history, payments, refunds and the lesson ledger shown inside a drawer). */
const reads = [
  route("(academic|scheduling|organization|identity|hr|crm|billing)/history/{id}"),
  route("billing/orders/{id}/(payments|refunds|refund-quote)"),
  route("billing/accounts/{id}/ledger"),
  route("billing/receipts/(receipt|refund)/{id}/prints"),
];

type Context = { params: Promise<{ path: string[] }> };

async function relay(request: NextRequest, { params }: Context, method: "GET" | "POST" | "PUT" | "DELETE") {
  const path = (await params).path.join("/");
  const allowed = method === "GET" ? reads : writes[method];
  if (!allowed.some((pattern) => pattern.test(path))) return NextResponse.json({ code: "unknown" }, { status: 404 });
  const token = await sessionToken("staff");
  if (!token) return NextResponse.json({ code: "unauthorized" }, { status: 401 });
  const body = method === "GET" || method === "DELETE" ? undefined : await request.text();
  const response = await apiFetch(`/api/${path}`, { method, token, body });
  const text = await response.text();
  if (response.ok) {
    return new NextResponse(text || "{}", { status: 200, headers: { "Content-Type": "application/json" } });
  }
  let code = response.status === 403 ? "forbidden" : "unknown";
  let errorParams: Record<string, unknown> = {};
  try {
    const parsed = JSON.parse(text) as { code?: string; params?: Record<string, unknown> };
    code = parsed.code ?? code;
    errorParams = parsed.params ?? {};
  } catch {
    // Empty or non-JSON error body: keep the status-based code.
  }
  return NextResponse.json({ code, params: errorParams }, { status: response.status === 403 ? 403 : 400 });
}

export const GET = (request: NextRequest, context: Context) => relay(request, context, "GET");
export const POST = (request: NextRequest, context: Context) => relay(request, context, "POST");
export const PUT = (request: NextRequest, context: Context) => relay(request, context, "PUT");
export const DELETE = (request: NextRequest, context: Context) => relay(request, context, "DELETE");
