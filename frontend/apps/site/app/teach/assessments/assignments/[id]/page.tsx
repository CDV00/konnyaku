import Link from "next/link";
import { notFound } from "next/navigation";
import { getFormatter, getTranslations } from "next-intl/server";
import { AlertTriangle, ArrowLeft, Users } from "lucide-react";
import { apiFetch, requireMe } from "@konnyaku/session";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@konnyaku/ui/components/ui/table";
import { EmptyState, PageHeader, Panel, StatusBadge } from "@konnyaku/ui/kk/blocks";
import { attemptTone } from "@/assessment/tones";
import type { AssignmentDetail } from "@/assessment/types";
import { AssignmentActions } from "./assignment-actions";

/** One assignment: its settings and every learner's progress, with links to grade. */
export default async function AssignmentPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const { token } = await requireMe("staff");
  const response = await apiFetch(`/api/assessment/assignments/${id}`, { token });
  if (!response.ok) notFound();
  const { assignment: a, students } = (await response.json()) as AssignmentDetail;
  const t = await getTranslations("assessment");
  const format = await getFormatter();
  const when = (iso: string | null) => (iso ? format.dateTime(new Date(iso), { dateStyle: "medium", timeStyle: "short" }) : "—");

  const settings: [string, string][] = [
    [t("assign.paper"), a.paperTitle],
    [t("assign.class"), a.classCode],
    [t("assign.target"), a.target === "Students" ? t("teach.someStudents", { count: a.studentIds.length }) : t(`targets.${a.target}`)],
    [t("assign.opensAt"), when(a.opensAt)],
    [t("assign.dueAt"), when(a.dueAt)],
    [t("assign.late"), a.latePolicy === "Penalty" ? t("teach.penaltyValue", { percent: a.latePenaltyPercent }) : t(`latePolicies.${a.latePolicy}`)],
    [t("assign.maxAttempts"), a.maxAttempts ? String(a.maxAttempts) : t("learn.noLimit")],
    [t("assign.scoring"), t(`scoringRules.${a.scoringRule}`)],
    [t("assign.timeLimit"), a.timeLimitMinutes ? t("learn.minutes", { minutes: a.timeLimitMinutes }) : "—"],
    [t("assign.grader"), t(`graders.${a.grader}`)],
    [t("assign.release"), a.scoreRelease === "AtTime" ? t("learn.releaseAt", { when: when(a.releaseAt) }) : t(`releases.${a.scoreRelease}`)],
    [t("assign.answers"), t(`answers.${a.answerVisibility}`)],
  ];

  return (
    <div className="space-y-6">
      <Link href="/teach/assessments" className="inline-flex items-center gap-1 text-sm text-muted-foreground hover:text-foreground">
        <ArrowLeft className="size-4" />
        {t("teach.title")}
      </Link>
      <PageHeader
        title={a.title}
        description={t(`kinds.${a.kind}`)}
        actions={<AssignmentActions id={a.id} canRelease={a.scoreRelease !== "OnSubmit"} canDelete={a.stats.started === 0} />}
      />

      <div className="grid gap-6 lg:grid-cols-[1fr_1.6fr]">
        <Panel title={t("assign.settings")}>
          <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-2 text-sm">
            {settings.map(([label, value]) => (
              <div key={label} className="contents">
                <dt className="text-muted-foreground">{label}</dt>
                <dd className="font-medium">{value}</dd>
              </div>
            ))}
          </dl>
        </Panel>

        <Panel
          title={t("teach.learners")}
          description={t("teach.progress", { submitted: a.stats.submitted, graded: a.stats.graded, total: a.stats.students })}
          bodyClassName="p-0"
        >
          {students.length === 0 ? (
            <div className="p-5">
              <EmptyState icon={Users} title={t("teach.noLearners")} />
            </div>
          ) : (
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>{t("teach.columns.learner")}</TableHead>
                  <TableHead>{t("teach.columns.status")}</TableHead>
                  <TableHead className="hidden sm:table-cell">{t("teach.columns.submitted")}</TableHead>
                  <TableHead className="text-right">{t("teach.columns.score")}</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {students.map((s) => (
                  <TableRow key={s.studentId}>
                    <TableCell>
                      {s.lastAttemptId ? (
                        <Link href={`/teach/assessments/attempts/${s.lastAttemptId}`} className="font-medium hover:text-primary">{s.studentName}</Link>
                      ) : (
                        <span className="font-medium">{s.studentName}</span>
                      )}
                      {s.attempts > 1 && <p className="text-xs text-muted-foreground">{t("teach.attempts", { count: s.attempts })}</p>}
                    </TableCell>
                    <TableCell>
                      <div className="flex flex-wrap items-center gap-1">
                        {s.lastStatus ? (
                          <StatusBadge tone={attemptTone[s.lastStatus]}>{t(`attemptStatus.${s.lastStatus}`)}</StatusBadge>
                        ) : (
                          <StatusBadge tone="muted">{t("teach.notStarted")}</StatusBadge>
                        )}
                        {s.late && <StatusBadge tone="warning">{t("learn.late")}</StatusBadge>}
                        {s.flags > 0 && (
                          <span className="inline-flex items-center gap-0.5 text-xs text-warning-foreground dark:text-warning" title={t("teach.flagsHint")}>
                            <AlertTriangle className="size-3.5" />
                            {s.flags}
                          </span>
                        )}
                      </div>
                    </TableCell>
                    <TableCell className="hidden tabular-nums sm:table-cell">{when(s.lastSubmittedAt)}</TableCell>
                    <TableCell className="text-right tabular-nums">
                      {s.finalScore !== null ? t("learn.scoreValue", { score: s.finalScore, max: s.maxScore ?? 0 }) : "—"}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
        </Panel>
      </div>
    </div>
  );
}
