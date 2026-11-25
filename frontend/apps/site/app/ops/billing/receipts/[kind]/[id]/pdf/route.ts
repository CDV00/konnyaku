import { NextResponse, type NextRequest } from "next/server";
import { getLocale } from "next-intl/server";
import { apiFetch, sessionToken } from "@konnyaku/session";

type Context = { params: Promise<{ kind: string; id: string }> };

const id = /^[0-9a-f-]{36}$/;

/**
 * Receipt PDF for staff, streamed from the API (which checks the branch, logs the print and stamps the operator).
 * `?download=1` saves the file; otherwise the browser opens it to print.
 */
export async function GET(request: NextRequest, { params }: Context) {
  const { kind, id: documentId } = await params;
  if ((kind !== "receipt" && kind !== "refund") || !id.test(documentId)) return NextResponse.json({ code: "unknown" }, { status: 404 });
  const token = await sessionToken("staff");
  if (!token) return NextResponse.redirect(new URL("/login?realm=staff", request.url));
  const download = request.nextUrl.searchParams.get("download") === "1";
  const locale = await getLocale();
  const response = await apiFetch(`/api/billing/receipts/${kind}/${documentId}/pdf?download=${download}&locale=${locale}`, { token });
  if (!response.ok) {
    const body = await response.text();
    return new NextResponse(body || "{}", { status: response.status, headers: { "Content-Type": "application/json" } });
  }
  return new NextResponse(response.body, {
    status: 200,
    headers: {
      "Content-Type": "application/pdf",
      "Content-Disposition": response.headers.get("Content-Disposition") ?? "inline",
      "Cache-Control": "no-store",
    },
  });
}
