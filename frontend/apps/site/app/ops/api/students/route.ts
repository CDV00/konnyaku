import { NextResponse, type NextRequest } from "next/server";
import { apiFetch, sessionToken } from "@konnyaku/session";

type Body = {
  fullName?: string;
  birthYear?: number;
  guardianName?: string;
  phone?: string;
  email?: string;
  homeBranchId?: string;
  createAccount?: boolean;
};

async function codeOf(response: Response) {
  const data = (await response.json().catch(() => ({}))) as { code?: string };
  return data.code ?? (response.status === 403 ? "forbidden" : "unknown");
}

/** Registers a student and, when asked, issues the learner sign-in account first so both stay linked. */
export async function POST(request: NextRequest) {
  const token = await sessionToken("staff");
  if (!token) return NextResponse.json({ code: "unauthorized" }, { status: 401 });
  const { createAccount, ...student } = (await request.json()) as Body;

  let account: { userId: string; username: string; temporaryPassword: string } | null = null;
  if (createAccount) {
    const created = await apiFetch("/api/identity/learners", {
      method: "POST",
      token,
      body: JSON.stringify({ displayName: student.fullName ?? "", role: "Student" }),
    });
    if (!created.ok) return NextResponse.json({ code: await codeOf(created) }, { status: 400 });
    account = await created.json();
  }

  const registered = await apiFetch("/api/crm/students", {
    method: "POST",
    token,
    body: JSON.stringify({ ...student, userId: account?.userId }),
  });
  if (!registered.ok) return NextResponse.json({ code: await codeOf(registered) }, { status: 400 });
  return NextResponse.json(account ? { username: account.username, temporaryPassword: account.temporaryPassword } : {});
}
