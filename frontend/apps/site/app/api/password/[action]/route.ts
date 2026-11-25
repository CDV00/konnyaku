import { NextResponse, type NextRequest } from "next/server";
import { apiFetch } from "@konnyaku/session";
import { errorFrom } from "../../session/sign-in";

const paths = { forgot: "/api/identity/password/forgot", reset: "/api/identity/password/reset" } as const;

/**
 * Self-service password reset for staff: `forgot` asks for a link by email (the answer is the same whether the email
 * exists or not), `reset` sets the new password with the link's token. The visitor IP goes along for the rate limit.
 */
export async function POST(request: NextRequest, { params }: { params: Promise<{ action: string }> }) {
  const { action } = await params;
  if (action !== "forgot" && action !== "reset") return NextResponse.json({ code: "unknown" }, { status: 404 });
  const response = await apiFetch(paths[action], { method: "POST", body: await request.text() });
  return response.ok ? NextResponse.json({ ok: true }) : errorFrom(response);
}
