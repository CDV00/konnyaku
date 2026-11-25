import Link from "next/link";
import { getFormatter, getTranslations } from "next-intl/server";
import { CheckCheck, Filter } from "lucide-react";
import { apiFetch, requireMe } from "@konnyaku/session";
import { Button } from "@konnyaku/ui/components/animate-ui/components/buttons/button";
import { Input } from "@konnyaku/ui/components/ui/input";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@konnyaku/ui/components/ui/table";
import { EmptyState, PageHeader, Panel, StatusBadge } from "@konnyaku/ui/kk/blocks";
import { Field, NativeSelect } from "@konnyaku/ui/kk/field";
import type { Assignment, QueueItem } from "@/assessment/types";
import { AssessmentTabs } from "../assessment-tabs";

type Search = { class?: string; dueFrom?: string; dueTo?: string };

/** Attempts waiting for the teacher (essays, mismatched short answers, teacher-graded assignments), oldest due first. */
export default async function GradingPage({ searchParams }: { searchParams: Promise<Search> }) {
  const { token } = await requireMe("staff");
  const search = await searchParams;
  const query = new URLSearchParams();
  if (search.class) query.set("classId", search.class);
  if (search.dueFrom) query.set("dueFrom", new Date(`${search.dueFrom}T00:00:00`).toISOString());
  if (search.dueTo) query.set("dueTo", new Date(`${search.dueTo}T23:59:59`).toISOString());
  const [queueResponse, assignmentsResponse] = await Promise.all([
    apiFetch(`/api/assessment/grading?${query.toString()}`, { token }),
    apiFetch("/api/assessment/assignments", { token }),
  ]);
  const queue = queueResponse.ok ? ((await queueResponse.json()) as QueueItem[]) : [];
  const assignments = assignmentsResponse.ok ? ((await assignmentsResponse.json()) as Assignment[]) : [];
  const classes = [...new Map(assignments.map((a) => [a.classId, a.classCode])).entries()].sort((a, b) => a[1].localeCompare(b[1]));
  const t = await getTranslations("assessment");
  const format = await getFormatter();
  const when = (iso: string | null) => (iso ? format.dateTime(new Date(iso), { day: "2-digit", month: "2-digit", hour: "2-digit", minute: "2-digit" }) : "—");

  return (
    <div className="space-y-6">
      <PageHeader title={t("grading.title")} description={t("grading.subtitle")} />
      <AssessmentTabs current="grading" />
      <form className="flex flex-wrap items-end gap-3" method="get">
        <Field label={t("grading.class")} className="w-44">
          <NativeSelect name="class" defaultValue={search.class ?? ""}>
            <option value="">{t("grading.allClasses")}</option>
            {classes.map(([id, code]) => <option key={id} value={id}>{code}</option>)}
          </NativeSelect>
        </Field>
        <Field label={t("grading.dueFrom")} className="w-40">
          <Input type="date" name="dueFrom" defaultValue={search.dueFrom} />
        </Field>
        <Field label={t("grading.dueTo")} className="w-40">
          <Input type="date" name="dueTo" defaultValue={search.dueTo} />
        </Field>
        <Button type="submit" variant="outline">
          <Filter />
          {t("grading.apply")}
        </Button>
      </form>
      <Panel bodyClassName="p-0">
        {queue.length === 0 ? (
          <div className="p-5">
            <EmptyState icon={CheckCheck} title={t("grading.empty")} />
          </div>
        ) : (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>{t("teach.columns.learner")}</TableHead>
                <TableHead>{t("teach.columns.title")}</TableHead>
                <TableHead className="hidden md:table-cell">{t("teach.columns.due")}</TableHead>
                <TableHead className="hidden sm:table-cell">{t("teach.columns.submitted")}</TableHead>
                <TableHead className="text-right">{t("grading.toGrade")}</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {queue.map((q) => (
                <TableRow key={q.attemptId}>
                  <TableCell>
                    <Link href={`/teach/assessments/attempts/${q.attemptId}`} className="font-medium hover:text-primary">{q.studentName}</Link>
                    <p className="text-xs text-muted-foreground">{q.classCode}</p>
                  </TableCell>
                  <TableCell>
                    <p>{q.assignmentTitle}</p>
                    <p className="text-xs text-muted-foreground">{t(`kinds.${q.kind}`)}</p>
                  </TableCell>
                  <TableCell className="hidden tabular-nums md:table-cell">{when(q.dueAt)}</TableCell>
                  <TableCell className="hidden tabular-nums sm:table-cell">
                    {when(q.submittedAt)}
                    {q.late && <StatusBadge tone="warning" className="ms-2">{t("learn.late")}</StatusBadge>}
                  </TableCell>
                  <TableCell className="text-right">
                    <StatusBadge tone={q.pendingItems > 0 ? "warning" : "primary"}>
                      {q.pendingItems > 0 ? t("grading.items", { count: q.pendingItems }) : t("grading.review")}
                    </StatusBadge>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </Panel>
    </div>
  );
}
