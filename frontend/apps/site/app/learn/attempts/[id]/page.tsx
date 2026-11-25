import { notFound, redirect } from "next/navigation";
import { apiFetch, requireMe } from "@konnyaku/session";
import type { AttemptPlay } from "@/assessment/types";
import { Player } from "./player";

/** Taking a test: one question per screen, the clock comes from the server. */
export default async function AttemptPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const { token } = await requireMe("learn");
  const response = await apiFetch(`/api/assessment/my/attempts/${id}`, { token });
  if (!response.ok) notFound();
  const attempt = (await response.json()) as AttemptPlay;
  if (attempt.status !== "InProgress") redirect(`/learn/attempts/${id}/result`);
  return <Player initial={attempt} />;
}
