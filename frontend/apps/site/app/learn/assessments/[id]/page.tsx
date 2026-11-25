import Link from "next/link";
import { notFound } from "next/navigation";
import { getFormatter, getTranslations } from "next-intl/server";
import { ArrowLeft, ChevronRight } from "lucide-react";
import { apiFetch, requireMe } from "@konnyaku/session";
import { PageHeader, Panel, StatusBadge } from "@konnyaku/ui/kk/blocks";
import { attemptTone, stateTone } from "@/assessment/tones";
import type { MyAssignmentDetail } from "@/assessment/types";
import { StartButton } from "./start-button";

/** One assignment for the learner: rules, attempts, start or resume. */
export default async function MyAssignmentPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const { token } = await requireMe("learn");
  const response = await apiFetch(`/api/assessment/my/assignments/${id}`, { token });
  if (!response.ok) notFound();
  const detail = (await response.json()) as MyAssignmentDetail;
  const a = detail.assignment;
  const t = await getTranslations("assessment");
  const format = await getFormatter();
  const when = (iso: string) => format.dateTime(new Date(iso), { weekday: "short", day: "2-digit", month: "2-digit", year: "numeric", hour: "2-digit", minute: "2-digit" });

  const rules: [string, string][] = [
    [t("learn.rules.kind"), t(`kinds.${a.kind}`)],
    [t("learn.rules.questions"), String(detail.paperQuestions)],
    [t("learn.rules.time"), detail.paperMinutes ? t("learn.minutes", { minutes: detail.paperMinutes }) : t("learn.noLimit")],
    [t("learn.rules.opens"), when(a.opensAt)],
    [t("learn.rules.due"), a.dueAt ? when(a.dueAt) : t("learn.noDue")],
    [t("learn.rules.late"), t(`latePolicies.${a.latePolicy as "Accept"}`)],
    [t("learn.rules.attempts"), a.maxAttempts ? t("learn.attemptsUsed", { used: a.attemptsUsed, max: a.maxAttempts }) : t("learn.attemptsUnlimited", { used: a.attemptsUsed })],
    [t("learn.rules.scoring"), t(`scoringRules.${detail.scoringRule as "Best"}`)],
    [t("learn.rules.release"), detail.releaseAt ? t("learn.releaseAt", { when: when(detail.releaseAt) }) : t(`releases.${detail.scoreRelease as "OnSubmit"}`)],
    [t("learn.rules.answers"), t(`answers.${detail.answerVisibility as "Never"}`)],
  ];

  return (
    <div className="space-y-6">
      <Link href="/learn/assessments" className="inline-flex items-center gap-1 text-sm text-muted-foreground hover:text-foreground">
        <ArrowLeft className="size-4" />
        {t("learn.title")}
      </Link>
      <PageHeader
        title={a.title}
        actions={<StatusBadge tone={stateTone[a.state]}>{t(`learnerState.${a.state}`)}</StatusBadge>}
      />

      <div className="grid gap-6 lg:grid-cols-[1fr_1.2fr]">
        <Panel title={t("learn.rules.title")}>
          <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-2 text-sm">
            {rules.map(([label, value]) => (
              <div key={label} className="contents">
                <dt className="text-muted-foreground">{label}</dt>
                <dd className="font-medium">{value}</dd>
              </div>
            ))}
          </dl>
          <div className="mt-5">
            {a.openAttemptId ? (
              <StartButton assignmentId={a.id} resume />
            ) : a.canStart ? (
              <StartButton assignmentId={a.id} />
            ) : (
              <p className="text-sm text-muted-foreground">{a.state === "Upcoming" ? t("learn.notOpenYet") : t("learn.cannotStart")}</p>
            )}
            {a.timeLimitMinutes || detail.paperMinutes ? <p className="mt-2 text-xs text-muted-foreground">{t("learn.timerHint")}</p> : null}
          </div>
        </Panel>

        <Panel title={t("learn.attempts")} bodyClassName="p-0">
          {detail.attempts.length === 0 ? (
            <p className="p-5 text-sm text-muted-foreground">{t("learn.noAttempts")}</p>
          ) : (
            <ul className="divide-y">
              {detail.attempts.map((attempt) => (
                <li key={attempt.id}>
                  <Link
                    href={attempt.status === "InProgress" ? `/learn/attempts/${attempt.id}` : `/learn/attempts/${attempt.id}/result`}
                    className="flex items-center gap-3 px-5 py-3 text-sm hover:bg-accent/50"
                  >
                    <div className="flex-1 space-y-0.5">
                      <p className="font-medium">{t("learn.attemptNumber", { number: attempt.number })}</p>
                      <p className="text-muted-foreground">{when(attempt.submittedAt ?? attempt.startedAt)}</p>
                    </div>
                    {attempt.late && <StatusBadge tone="warning">{t("learn.late")}</StatusBadge>}
                    {attempt.score !== null ? (
                      <span className="font-medium tabular-nums">{t("learn.scoreValue", { score: attempt.score, max: attempt.maxScore })}</span>
                    ) : (
                      <StatusBadge tone={attemptTone[attempt.status]}>{t(`attemptStatus.${attempt.status}`)}</StatusBadge>
                    )}
                    <ChevronRight className="size-4 text-muted-foreground" />
                  </Link>
                </li>
              ))}
            </ul>
          )}
        </Panel>
      </div>
    </div>
  );
}
