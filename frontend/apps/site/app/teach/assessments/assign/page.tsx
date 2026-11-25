import { getTranslations } from "next-intl/server";
import { apiFetch, requireMe } from "@konnyaku/session";
import { PageHeader } from "@konnyaku/ui/kk/blocks";
import type { Paper } from "@/assessment/types";
import { AssessmentTabs } from "../assessment-tabs";
import { AssignForm, type ClassOption, type SessionOption, type StudentOption } from "./assign-form";

type Page<T> = { items: T[] };

async function json<T>(path: string, token: string, fallback: T): Promise<T> {
  const response = await apiFetch(path, { token });
  return response.ok ? ((await response.json()) as T) : fallback;
}

/** Give a paper to a class, one of its sessions or some of its learners. The class choice reloads its sessions and learners. */
export default async function AssignPage({ searchParams }: { searchParams: Promise<{ paper?: string; class?: string }> }) {
  const { token } = await requireMe("staff");
  const query = await searchParams;
  const [papers, classes] = await Promise.all([
    json<Paper[]>("/api/assessment/papers", token, []),
    json<Page<ClassOption>>("/api/academic/classes?status=current&pageSize=200&sort=code", token, { items: [] }),
  ]);
  const classId = classes.items.find((c) => c.id === query.class)?.id ?? null;
  const [sessions, students] = classId
    ? await Promise.all([
        json<SessionOption[]>(`/api/scheduling/classes/${classId}/sessions`, token, []),
        json<StudentOption[]>(`/api/assessment/classes/${classId}/students`, token, []),
      ])
    : [[], []];
  const t = await getTranslations("assessment");

  return (
    <div className="space-y-6">
      <PageHeader title={t("assign.title")} description={t("assign.subtitle")} />
      <AssessmentTabs current="assignments" />
      <AssignForm
        papers={papers.filter((p) => !p.archived)}
        classes={classes.items}
        classId={classId}
        paperId={query.paper ?? null}
        sessions={sessions.filter((s) => s.status !== "Cancelled")}
        students={students}
      />
    </div>
  );
}
