import { NextResponse, type NextRequest } from "next/server";
import { getLocale } from "next-intl/server";
import { apiFetch, sessionToken } from "@konnyaku/session";

type Context = { params: Promise<{ kind: string; id: string }> };

const id = /^[0-9a-f-]{36}$/;

/** A learner's own receipt as a PDF download (the API checks the order belongs to them and logs the download). */
export async function GET(request: NextRequest, { params }: Context) {
  const { kind, id: documentId } = await params;
  if ((kind !== "receipt" && kind !== "refund") || !id.test(documentId)) return NextResponse.json({ code: "unknown" }, { status: 404 });
  const token = await sessionToken("learn");
  if (!token) return NextResponse.redirect(new URL("/login?realm=learn", request.url));
  const locale = await getLocale();
  const response = await apiFetch(`/api/billing/my/receipts/${kind}/${documentId}/pdf?download=true&locale=${locale}`, { token });
  if (!response.ok) return NextResponse.redirect(new URL("/learn/payments?error=download", request.url));
  return new NextResponse(response.body, {
    status: 200,
    headers: {
      "Content-Type": "application/pdf",
      "Content-Disposition": response.headers.get("Content-Disposition") ?? "attachment",
      "Cache-Control": "no-store",
    },
  });
}
