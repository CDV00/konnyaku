import { NextResponse, type NextRequest } from "next/server";
import { apiFetch, sessionToken, type Realm } from "@konnyaku/session";
import { pushCookie, pushCookieOptions } from "../../push";

/**
 * The signed-in person's own notifications, for the bell, the inbox page and the settings page of every area:
 * forwards to /api/notification/* with the session of the realm. Only these routes, only these methods.
 */
const allowed: { method: string; pattern: RegExp }[] = [
  { method: "GET", pattern: /^inbox$/ },
  { method: "GET", pattern: /^inbox\/unread-count$/ },
  { method: "GET", pattern: /^inbox\/sync$/ },
  { method: "POST", pattern: /^inbox\/[0-9a-f-]{36}\/read$/ },
  { method: "POST", pattern: /^inbox\/read-all$/ },
  { method: "GET", pattern: /^preferences$/ },
  { method: "PUT", pattern: /^preferences$/ },
  { method: "GET", pattern: /^push\/config$/ },
  { method: "POST", pattern: /^push\/subscriptions$/ },
  { method: "DELETE", pattern: /^push\/subscriptions$/ },
  { method: "POST", pattern: /^realtime\/ticket$/ },
];

type Params = { params: Promise<{ realm: string; path: string[] }> };

async function forward(request: NextRequest, { params }: Params): Promise<Response> {
  const { realm, path } = await params;
  const route = path.join("/");
  if ((realm !== "learn" && realm !== "staff") || !allowed.some((a) => a.method === request.method && a.pattern.test(route))) {
    return NextResponse.json({ code: "unknown" }, { status: 404 });
  }
  const token = await sessionToken(realm as Realm);
  if (!token) return NextResponse.json({ code: "unauthorized" }, { status: 401 });

  const query = request.nextUrl.search;
  const body = request.method === "GET" || request.method === "DELETE" ? undefined : await request.text();
  // Turning push off on this device: the endpoint comes from the cookie set when it was turned on.
  const endpoint = request.cookies.get(pushCookie[realm as Realm])?.value;
  const target = route === "push/subscriptions" && request.method === "DELETE"
    ? `/api/notification/push/subscriptions?endpoint=${encodeURIComponent(endpoint ?? "")}`
    : `/api/notification/${route}${query}`;
  const response = await apiFetch(target, { method: request.method, token, body: body || undefined });
  const text = await response.text();
  const result = new NextResponse(text || null, {
    status: response.status,
    headers: { "Content-Type": response.headers.get("Content-Type") ?? "application/json", "Cache-Control": "no-store" },
  });

  if (route === "push/subscriptions" && response.ok) {
    if (request.method === "POST") {
      const subscribed = JSON.parse(body ?? "{}") as { endpoint?: string };
      if (subscribed.endpoint) result.cookies.set(pushCookie[realm as Realm], subscribed.endpoint, pushCookieOptions);
    } else {
      result.cookies.delete(pushCookie[realm as Realm]);
    }
  }
  return result;
}

export const GET = forward;
export const POST = forward;
export const PUT = forward;
export const DELETE = forward;
