import Link from "next/link";
import { getFormatter, getTranslations } from "next-intl/server";
import { ChevronRight, ClipboardCheck, ClipboardList } from "lucide-react";
import { apiFetch, requireMe } from "@konnyaku/session";
import { EmptyState, PageHeader, Panel, StatusBadge } from "@konnyaku/ui/kk/blocks";
import { stateTone } from "@/assessment/tones";
import type { MyAssignment } from "@/assessment/types";

/** "Bài tập & kiểm tra": what to do now, then what was handed in. */
export default async function MyAssessmentsPage() {
  const { token } = await requireMe("learn");
  const response = await apiFetch("/api/assessment/my/assignments", { token });
  const assignments = response.ok ? ((await response.json()) as MyAssignment[]) : [];
  const t = await getTranslations("assessment");
  const format = await getFormatter();
  const todo = assignments.filter((a) => a.state === "Open" || a.state === "InProgress" || a.state === "Upcoming");
  const done = assignments.filter((a) => !todo.includes(a));

  const list = (rows: MyAssignment[]) => (
    <ul className="divide-y">
      {rows.map((a) => (
        <li key={a.id}>
          <Link href={`/learn/assessments/${a.id}`} className="flex items-center gap-3 px-5 py-4 transition-colors hover:bg-accent/50">
            <div className="min-w-0 flex-1 space-y-1">
              <p className="truncate font-medium">{a.title}</p>
              <p className="text-sm text-muted-foreground">
                {t(`kinds.${a.kind}`)}
                {a.dueAt && <> · {t("learn.dueOn", { when: format.dateTime(new Date(a.dueAt), { weekday: "short", day: "2-digit", month: "2-digit", hour: "2-digit", minute: "2-digit" }) })}</>}
              </p>
            </div>
            <div className="flex shrink-0 flex-col items-end gap-1">
              <StatusBadge tone={stateTone[a.state]}>{t(`learnerState.${a.state}`)}</StatusBadge>
              {a.scoreVisible && a.score !== null && (
                <span className="text-sm font-medium tabular-nums">{t("learn.scoreValue", { score: a.score, max: a.maxScore ?? 0 })}</span>
              )}
            </div>
            <ChevronRight className="size-4 shrink-0 text-muted-foreground" />
          </Link>
        </li>
      ))}
    </ul>
  );

  return (
    <div className="space-y-6">
      <PageHeader title={t("learn.title")} description={t("learn.subtitle")} />
      <Panel title={t("learn.todo")} bodyClassName="p-0">
        {todo.length === 0 ? (
          <div className="p-5">
            <EmptyState icon={ClipboardCheck} title={t("learn.todoEmpty")} />
          </div>
        ) : (
          list(todo)
        )}
      </Panel>
      <Panel title={t("learn.done")} bodyClassName="p-0">
        {done.length === 0 ? (
          <div className="p-5">
            <EmptyState icon={ClipboardList} title={t("learn.doneEmpty")} />
          </div>
        ) : (
          list(done)
        )}
      </Panel>
    </div>
  );
}
