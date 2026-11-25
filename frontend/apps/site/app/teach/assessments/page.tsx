import Link from "next/link";
import { getFormatter, getTranslations } from "next-intl/server";
import { ClipboardCheck, Plus } from "lucide-react";
import { apiFetch, requireMe } from "@konnyaku/session";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@konnyaku/ui/components/ui/table";
import { EmptyState, PageHeader, Panel, StatusBadge } from "@konnyaku/ui/kk/blocks";
import { LinkButton } from "@/assessment/link-button";
import type { Assignment } from "@/assessment/types";
import { AssessmentTabs } from "./assessment-tabs";

const filters = ["open", "upcoming", "past", "all"] as const;

/** Assignments of the classes the teacher teaches (and, for managers, of their branches). */
export default async function AssignmentsPage({ searchParams }: { searchParams: Promise<{ status?: string }> }) {
  const { token } = await requireMe("staff");
  const requested = (await searchParams).status;
  const status = filters.find((f) => f === requested) ?? "open";
  const response = await apiFetch(`/api/assessment/assignments?status=${status === "all" ? "" : status}`, { token });
  const assignments = response.ok ? ((await response.json()) as Assignment[]) : [];
  const t = await getTranslations("assessment");
  const format = await getFormatter();
  const when = (iso: string | null) =>
    iso ? format.dateTime(new Date(iso), { day: "2-digit", month: "2-digit", hour: "2-digit", minute: "2-digit" }) : "—";

  return (
    <div className="space-y-6">
      <PageHeader
        title={t("teach.title")}
        description={t("teach.subtitle")}
        actions={
          <LinkButton href="/teach/assessments/assign">
            <Plus />
            {t("teach.assign")}
          </LinkButton>
        }
      />
      <AssessmentTabs current="assignments" />
      <div className="flex flex-wrap gap-2" role="group" aria-label={t("teach.filter")}>
        {filters.map((f) => (
          <LinkButton
            key={f}
            href={`/teach/assessments?status=${f}`}
            aria-current={f === status ? "true" : undefined}
            variant={f === status ? "secondary" : "ghost"}
            size="sm"
          >
            {t(`teach.filters.${f}`)}
          </LinkButton>
        ))}
      </div>
      <Panel bodyClassName="p-0">
        {assignments.length === 0 ? (
          <div className="p-5">
            <EmptyState icon={ClipboardCheck} title={t("teach.empty")} description={t("teach.emptyHint")} />
          </div>
        ) : (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>{t("teach.columns.title")}</TableHead>
                <TableHead className="hidden md:table-cell">{t("teach.columns.class")}</TableHead>
                <TableHead className="hidden lg:table-cell">{t("teach.columns.opens")}</TableHead>
                <TableHead>{t("teach.columns.due")}</TableHead>
                <TableHead className="text-right">{t("teach.columns.progress")}</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {assignments.map((a) => (
                <TableRow key={a.id}>
                  <TableCell>
                    <Link href={`/teach/assessments/assignments/${a.id}`} className="font-medium hover:text-primary">{a.title}</Link>
                    <p className="text-xs text-muted-foreground">
                      {t(`kinds.${a.kind}`)}
                      <span className="md:hidden"> · {a.classCode}</span>
                    </p>
                  </TableCell>
                  <TableCell className="hidden md:table-cell">{a.classCode}</TableCell>
                  <TableCell className="hidden tabular-nums lg:table-cell">{when(a.opensAt)}</TableCell>
                  <TableCell className="tabular-nums">{when(a.dueAt)}</TableCell>
                  <TableCell className="text-right">
                    <div className="flex flex-col items-end gap-1">
                      <span className="text-sm tabular-nums">{t("teach.submittedOf", { submitted: a.stats.submitted, total: a.stats.students })}</span>
                      {a.stats.awaitingGrading > 0 && <StatusBadge tone="warning">{t("teach.toGrade", { count: a.stats.awaitingGrading })}</StatusBadge>}
                    </div>
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
