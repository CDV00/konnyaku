import { joinSession } from "@konnyaku/session";

export async function GET(_: Request, { params }: { params: Promise<{ sessionId: string }> }) {
  return joinSession("learn", (await params).sessionId, "/learn");
}
