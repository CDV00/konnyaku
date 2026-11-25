import { NextResponse, type NextRequest } from "next/server";
import { getLocale } from "next-intl/server";
import { apiFetch, sessionToken } from "@konnyaku/session";

const id = "[0-9a-f-]{36}";

/** Files the ops screens may download: list exports (CSV) and data subject exports (JSON). The API checks every permission and logs. */
const targets: { pattern: RegExp; api: (match: RegExpExecArray) => string; withQuery: boolean }[] = [
  { pattern: /^(students|leads)$/, api: (m) => `/api/crm/${m[1]}/export`, withQuery: true },
  { pattern: new RegExp(`^privacy/(students|leads)/(${id})$`), api: (m) => `/api/crm/privacy/${m[1]}/${m[2]}/export`, withQuery: false },
];

type Context = { params: Promise<{ path: string[] }> };

export async function GET(request: NextRequest, { params }: Context) {
  const path = (await params).path.join("/");
  const target = targets.map((t) => ({ t, m: t.pattern.exec(path) })).find((x) => x.m);
  if (!target?.m) return NextResponse.json({ code: "unknown" }, { status: 404 });
  const token = await sessionToken("staff");
  if (!token) return NextResponse.json({ code: "unauthorized" }, { status: 401 });

  const query = new URLSearchParams(target.t.withQuery ? request.nextUrl.searchParams : undefined);
  if (target.t.withQuery) query.set("locale", await getLocale());
  const suffix = query.toString() ? `?${query.toString()}` : "";
  const response = await apiFetch(`${target.t.api(target.m)}${suffix}`, { token });
  if (!response.ok) {
    const data = (await response.json().catch(() => ({}))) as { code?: string; params?: Record<string, unknown> };
    const code = data.code ?? (response.status === 403 ? "forbidden" : "unknown");
    return NextResponse.json({ code, params: data.params ?? {} }, { status: response.status === 403 ? 403 : 400 });
  }
  // Personal data: never cached by the browser or a proxy.
  return new NextResponse(response.body, {
    status: 200,
    headers: {
      "Content-Type": response.headers.get("Content-Type") ?? "application/octet-stream",
      "Content-Disposition": response.headers.get("Content-Disposition") ?? "attachment",
      "Cache-Control": "no-store",
    },
  });
}
