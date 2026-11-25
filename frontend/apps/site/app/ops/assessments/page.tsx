import { getFormatter, getTranslations } from "next-intl/server";
import { ClipboardCheck, Hourglass, Percent, PlayCircle } from "lucide-react";
import { requireMe } from "@konnyaku/session";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@konnyaku/ui/components/ui/table";
import { EmptyState, PageHeader, Panel, StatCard, StatusBadge } from "@konnyaku/ui/kk/blocks";
import { Fade } from "@konnyaku/ui/components/animate-ui/primitives/effects/fade";
import type { Overview } from "@/assessment/types";
import { getJson } from "../lib";

/**
 * Read-only figures of tests and homework in the branches the manager runs. Teachers build and grade in their own area
 * (`/teach/assessments`); nothing is changed from here.
 */
export default async function AssessmentsOverviewPage() {
  const { token } = await requireMe("staff");
  const overview = await getJson<Overview | null>("/api/assessment/overview", token, null);
  const t = await getTranslations("assessment");
  const format = await getFormatter();
  const when = (iso: string | null) => (iso ? format.dateTime(new Date(iso), { day: "2-digit", month: "2-digit", year: "numeric" }) : "—");

  if (!overview) {
    return (
      <div className="space-y-6">
        <PageHeader title={t("ops.title")} />
        <EmptyState icon={ClipboardCheck} title={t("ops.forbidden")} />
      </div>
    );
  }

  return (
    <div className="space-y-6">
      <PageHeader title={t("ops.title")} description={t("ops.subtitle")} />
      <Fade inView className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
        <StatCard label={t("ops.assignments")} count={overview.assignments} icon={ClipboardCheck} />
        <StatCard label={t("ops.openNow")} count={overview.openNow} icon={PlayCircle} tone="success" />
        <StatCard label={t("ops.awaiting")} count={overview.awaitingGrading} icon={Hourglass} tone={overview.awaitingGrading > 0 ? "warning" : "muted"} />
        <StatCard
          label={t("ops.average")}
          value={overview.averagePercent !== null ? t("learn.percent", { percent: overview.averagePercent }) : "—"}
          icon={Percent}
          tone="highlight"
          hint={t("ops.averageHint", { count: overview.gradedLast30Days })}
        />
      </Fade>

      {overview.byKind.length > 0 && (
        <div className="flex flex-wrap gap-2">
          {overview.byKind.map((k) => (
            <StatusBadge key={k.kind} tone="primary">{t("ops.kindCount", { kind: t(`kinds.${k.kind}`), count: k.count })}</StatusBadge>
          ))}
        </div>
      )}

      <Panel title={t("ops.recent")} bodyClassName="p-0">
        {overview.recent.length === 0 ? (
          <div className="p-5">
            <EmptyState icon={ClipboardCheck} title={t("teach.empty")} />
          </div>
        ) : (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>{t("teach.columns.title")}</TableHead>
                <TableHead>{t("teach.columns.class")}</TableHead>
                <TableHead className="hidden md:table-cell">{t("teach.columns.opens")}</TableHead>
                <TableHead className="hidden md:table-cell">{t("teach.columns.due")}</TableHead>
                <TableHead className="text-right">{t("teach.columns.progress")}</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {overview.recent.map((a) => (
                <TableRow key={a.id}>
                  <TableCell>
                    <p className="font-medium">{a.title}</p>
                    <p className="text-xs text-muted-foreground">{t(`kinds.${a.kind}`)}</p>
                  </TableCell>
                  <TableCell>{a.classCode}</TableCell>
                  <TableCell className="hidden tabular-nums md:table-cell">{when(a.opensAt)}</TableCell>
                  <TableCell className="hidden tabular-nums md:table-cell">{when(a.dueAt)}</TableCell>
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
