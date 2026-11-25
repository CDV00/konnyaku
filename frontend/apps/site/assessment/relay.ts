import { NextResponse, type NextRequest } from "next/server";
import { apiFetch, sessionToken, type Realm } from "@konnyaku/session";

export type RelayContext = { params: Promise<{ path: string[] }> };

type Method = "GET" | "POST" | "PUT" | "DELETE";

const id = "[0-9a-f-]{36}";
export const route = (pattern: string) => new RegExp(`^${pattern.replaceAll("{id}", id)}$`);

/**
 * Forwards a browser call of a teach / learn screen to the Assessment API with the realm's session token. Only the
 * listed paths are forwarded; the API checks every permission again. Errors keep the API's `{ code, params }`.
 */
export function assessmentRelay(realm: Realm, prefix: string, allowed: Record<Method, RegExp[]>) {
  async function relay(request: NextRequest, { params }: RelayContext, method: Method) {
    const path = (await params).path.join("/");
    if (!allowed[method].some((pattern) => pattern.test(path))) return NextResponse.json({ code: "unknown" }, { status: 404 });
    const token = await sessionToken(realm);
    if (!token) return NextResponse.json({ code: "unauthorized" }, { status: 401 });
    const body = method === "GET" || method === "DELETE" ? undefined : await request.text();
    const query = method === "GET" ? request.nextUrl.search : "";
    const response = await apiFetch(`${prefix}/${path}${query}`, { method, token, body });
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
    return NextResponse.json({ code, params: errorParams }, { status: response.status === 404 ? 404 : response.status === 403 ? 403 : 400 });
  }

  return {
    GET: (request: NextRequest, context: RelayContext) => relay(request, context, "GET"),
    POST: (request: NextRequest, context: RelayContext) => relay(request, context, "POST"),
    PUT: (request: NextRequest, context: RelayContext) => relay(request, context, "PUT"),
    DELETE: (request: NextRequest, context: RelayContext) => relay(request, context, "DELETE"),
  };
}
