import Link from "next/link";
import { notFound } from "next/navigation";
import { getFormatter, getLocale, getTranslations } from "next-intl/server";
import { ArrowLeft, Award, CheckCircle2, Clock, EyeOff, Hourglass, MessageSquare, XCircle } from "lucide-react";
import { apiFetch, requireMe } from "@konnyaku/session";
import { PageHeader, Panel, StatCard, StatusBadge } from "@konnyaku/ui/kk/blocks";
import { Fade } from "@konnyaku/ui/components/animate-ui/primitives/effects/fade";
import { AnswerReview, RichHtml } from "@/assessment/render";
import { skillNames } from "@/assessment/skills";
import type { AttemptResult } from "@/assessment/types";

/** Result of one attempt: score per skill, then every question with what the release settings allow. */
export default async function ResultPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const { token } = await requireMe("learn");
  const response = await apiFetch(`/api/assessment/my/attempts/${id}/result`, { token });
  if (!response.ok) notFound();
  const result = (await response.json()) as AttemptResult;
  const t = await getTranslations("assessment");
  const format = await getFormatter();
  const skills = await skillNames(token, await getLocale());
  const percent = result.score !== null && result.maxScore > 0 ? Math.round((result.score / result.maxScore) * 100) : null;
  const sectionTitle = (index: number) => result.sections[index]?.title;

  return (
    <div className="space-y-6">
      <Link href={`/learn/assessments/${result.assignmentId}`} className="inline-flex items-center gap-1 text-sm text-muted-foreground hover:text-foreground">
        <ArrowLeft className="size-4" />
        {t("learn.backToAssignment")}
      </Link>
      <PageHeader
        title={result.title}
        description={t("learn.attemptOf", {
          number: result.number,
          kind: t(`kinds.${result.kind}`),
          when: result.submittedAt ? format.dateTime(new Date(result.submittedAt), { dateStyle: "medium", timeStyle: "short" }) : "—",
        })}
        actions={
          <div className="flex flex-wrap gap-2">
            {result.late && <StatusBadge tone="warning">{t("learn.late")}</StatusBadge>}
            {result.autoSubmitted && <StatusBadge tone="muted">{t("learn.autoSubmitted")}</StatusBadge>}
          </div>
        }
      />

      {!result.scoresVisible ? (
        <Panel>
          <div className="flex items-start gap-3">
            <EyeOff className="mt-0.5 size-5 text-muted-foreground" />
            <div className="space-y-1 text-sm">
              <p className="font-medium">{t("learn.scoresHidden")}</p>
              <p className="text-muted-foreground">
                {result.releaseAt
                  ? t("learn.releaseAt", { when: format.dateTime(new Date(result.releaseAt), { dateStyle: "medium", timeStyle: "short" }) })
                  : t("learn.releaseAfterGrading")}
              </p>
            </div>
          </div>
        </Panel>
      ) : (
        <>
          <Fade inView className="grid gap-4 sm:grid-cols-3">
            <StatCard
              label={t("learn.score")}
              value={result.score !== null ? t("learn.scoreValue", { score: result.score, max: result.maxScore }) : t("learn.pending")}
              icon={result.score !== null ? Award : Hourglass}
              tone={result.score !== null ? "success" : "warning"}
              hint={percent !== null ? t("learn.percent", { percent }) : t("learn.pendingItems", { count: result.pendingItems })}
            />
            <StatCard label={t("learn.correct")} count={result.items.filter((i) => i.correct).length} icon={CheckCircle2} hint={t("learn.ofQuestions", { total: result.items.length })} />
            <StatCard
              label={t("learn.penalty")}
              value={result.penaltyPercent > 0 ? t("learn.penaltyValue", { percent: result.penaltyPercent }) : "—"}
              icon={Clock}
              tone={result.penaltyPercent > 0 ? "warning" : "muted"}
            />
          </Fade>

          {result.skills.length > 0 && (
            <Panel title={t("learn.bySkill")}>
              <ul className="space-y-3">
                {result.skills.map((s) => {
                  const share = s.maxScore > 0 ? Math.round((s.score / s.maxScore) * 100) : 0;
                  return (
                    <li key={s.skillCode} className="space-y-1">
                      <div className="flex items-center justify-between text-sm">
                        <span className="font-medium">{s.skillCode ? (skills.get(s.skillCode) ?? s.skillCode) : t("learn.general")}</span>
                        <span className="tabular-nums text-muted-foreground">{t("learn.scoreValue", { score: s.score, max: s.maxScore })}</span>
                      </div>
                      <div className="h-2 overflow-hidden rounded-full bg-muted" role="progressbar" aria-valuenow={share} aria-valuemin={0} aria-valuemax={100}>
                        <div className="h-full rounded-full bg-primary" style={{ width: `${share}%` }} />
                      </div>
                    </li>
                  );
                })}
              </ul>
            </Panel>
          )}

          {result.comment && (
            <Panel title={t("learn.teacherComment")}>
              <p className="flex items-start gap-2 whitespace-pre-wrap text-sm">
                <MessageSquare className="mt-0.5 size-4 shrink-0 text-muted-foreground" />
                {result.comment}
              </p>
            </Panel>
          )}
        </>
      )}

      <Panel title={t("learn.questions")} description={result.answersVisible ? t("learn.answersShown") : t("learn.answersHidden")}>
        <ol className="space-y-5">
          {result.items.map((item, index) => (
            <li key={item.id} className="space-y-3 border-b pb-5 last:border-0 last:pb-0">
              {(index === 0 || result.items[index - 1]?.sectionIndex !== item.sectionIndex) && sectionTitle(item.sectionIndex) && (
                <h3 className="text-sm font-semibold text-primary">{sectionTitle(item.sectionIndex)}</h3>
              )}
              <div className="flex items-start justify-between gap-3">
                <p className="text-xs font-medium text-muted-foreground">
                  {t("player.question", { number: index + 1, total: result.items.length })} · {t(`types.${item.type}`)}
                </p>
                {item.state === "NeedsTeacher" ? (
                  <StatusBadge tone="warning">{t("learn.waitingTeacher")}</StatusBadge>
                ) : item.score !== null ? (
                  <span className="flex items-center gap-1 text-sm font-medium tabular-nums">
                    {item.correct ? <CheckCircle2 className="size-4 text-success" /> : <XCircle className="size-4 text-destructive" />}
                    {t("learn.scoreValue", { score: item.score, max: item.points })}
                  </span>
                ) : null}
              </div>
              {item.passage && (index === 0 || result.items[index - 1]?.passage !== item.passage) && (
                <RichHtml html={item.passage} hideRuby={!result.showRuby} className="rounded-lg bg-muted/40 px-3 py-2 text-sm" />
              )}
              {item.type !== "FillBlank" && <RichHtml html={item.stem} hideRuby={!result.showRuby} />}
              <AnswerReview item={item} hideRuby={!result.showRuby} />
              {item.explanation && (
                <div className="rounded-lg border border-primary/20 bg-primary/5 px-3 py-2 text-sm">
                  <p className="mb-1 text-xs font-medium text-primary">{t("learn.explanation")}</p>
                  <RichHtml html={item.explanation} hideRuby={!result.showRuby} />
                </div>
              )}
              {item.comment && <p className="whitespace-pre-wrap text-sm text-muted-foreground">{t("learn.itemComment", { comment: item.comment })}</p>}
            </li>
          ))}
        </ol>
      </Panel>
    </div>
  );
}
