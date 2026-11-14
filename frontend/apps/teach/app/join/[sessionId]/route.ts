import { NextResponse, type NextRequest } from "next/server";
import { headers } from "next/headers";
import { apiFetch, sessionToken } from "@konnyaku/session";

/** Entry point of every class: the API checks this person may attend, then we redirect to the meeting. */
export async function GET(_: NextRequest, { params }: { params: Promise<{ sessionId: string }> }) {
  const { sessionId } = await params;
  const origin = (await headers()).get("x-site-origin") ?? "";
  const token = await sessionToken("staff");
  if (!token) return NextResponse.redirect(`${origin}/login?realm=staff`);
  const response = await apiFetch(`/api/workspace/sessions/${sessionId}/join`, { token });
  if (!response.ok) return NextResponse.redirect(`${origin}/teach?meeting=unavailable`);
  const { meetingUri } = (await response.json()) as { meetingUri: string };
  return NextResponse.redirect(meetingUri.startsWith("/") ? `${origin}${meetingUri}` : meetingUri);
}
