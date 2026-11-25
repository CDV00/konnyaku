import { notFound } from "next/navigation";
import { getLocale } from "next-intl/server";
import { apiFetch, requireMe } from "@konnyaku/session";
import { skillNames } from "@/assessment/skills";
import type { AttemptReview } from "@/assessment/types";
import { GradeAttempt } from "./grade-attempt";

/** The teacher grades one attempt: essay and short-answer items, rubric bands, comments, then finishes. */
export default async function AttemptReviewPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const { token } = await requireMe("staff");
  const response = await apiFetch(`/api/assessment/attempts/${id}`, { token });
  if (!response.ok) notFound();
  const review = (await response.json()) as AttemptReview;
  const skills = await skillNames(token, await getLocale());
  return <GradeAttempt initial={review} skills={Object.fromEntries(skills)} />;
}
