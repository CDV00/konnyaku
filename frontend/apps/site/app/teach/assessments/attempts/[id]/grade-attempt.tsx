"use client";

import { useState, type FormEvent } from "react";
import Link from "next/link";
import { useFormatter, useTranslations } from "next-intl";
import { ArrowLeft, CheckCircle2, ClipboardPaste, EyeOff, Save } from "lucide-react";
import { Button } from "@konnyaku/ui/components/animate-ui/components/buttons/button";
import { Input } from "@konnyaku/ui/components/ui/input";
import { Textarea } from "@konnyaku/ui/components/ui/textarea";
import { FormMessage, PageHeader, Panel, StatusBadge } from "@konnyaku/ui/kk/blocks";
import { Field, NativeSelect } from "@konnyaku/ui/kk/field";
import { cn } from "@konnyaku/ui/lib/utils";
import { callApi } from "@/assessment/client";
import { AnswerReview, RichHtml } from "@/assessment/render";
import { countText } from "@/assessment/rich-text";
import { attemptTone } from "@/assessment/tones";
import { errorKey, type AttemptReview, type ResultItem } from "@/assessment/types";

export function GradeAttempt({ initial, skills }: { initial: AttemptReview; skills: Record<string, string> }) {
  const t = useTranslations("assessment");
  const format = useFormatter();
  const [review, setReview] = useState(initial);
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState(false);
  const submitted = review.status !== "InProgress";

  function showError(code: string) {
    const key = `errors.${errorKey(code)}`;
    setError(t.has(key) ? t(key as "errors.unknown") : t("errors.unknown"));
  }

  async function finalize(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const comment = String(new FormData(event.currentTarget).get("comment") ?? "");
    setPending(true);
    setError(null);
    const result = await callApi<AttemptReview>(`/teach/api/assessment/attempts/${review.id}/finalize`, "POST", { comment });
    setPending(false);
    if (result.ok) setReview(result.data);
    else showError(result.code);
  }

  const when = (iso: string | null) => (iso ? format.dateTime(new Date(iso), { dateStyle: "medium", timeStyle: "short" }) : "—");

  return (
    <div className="space-y-6">
      <Link href={`/teach/assessments/assignments/${review.assignmentId}`} className="inline-flex items-center gap-1 text-sm text-muted-foreground hover:text-foreground">
        <ArrowLeft className="size-4" />
        {review.assignmentTitle}
      </Link>
      <PageHeader
        title={review.studentName}
        description={t("grade.attemptLine", { number: review.number, started: when(review.startedAt), submitted: when(review.submittedAt) })}
        actions={
          <div className="flex flex-wrap gap-2">
            <StatusBadge tone={attemptTone[review.status]}>{t(`attemptStatus.${review.status}`)}</StatusBadge>
            {review.late && <StatusBadge tone="warning">{t("learn.late")}</StatusBadge>}
            {review.autoSubmitted && <StatusBadge tone="muted">{t("learn.autoSubmitted")}</StatusBadge>}
          </div>
        }
      />

      <div className="grid gap-6 lg:grid-cols-[1fr_20rem]">
        <ol className="space-y-4">
          {review.items.map((item, index) => (
            <li key={item.id}>
              <ItemCard
                item={item}
                index={index}
                total={review.items.length}
                attemptId={review.id}
                submitted={submitted}
                flagged={review.events.some((e) => e.itemId === item.id)}
                onSaved={(next) => {
                  setReview(next);
                  setError(null);
                }}
                onError={showError}
              />
            </li>
          ))}
        </ol>

        <div className="space-y-4 lg:sticky lg:top-20 lg:self-start">
          <Panel title={t("grade.summary")}>
            <dl className="space-y-2 text-sm">
              <div className="flex justify-between">
                <dt className="text-muted-foreground">{t("grade.raw")}</dt>
                <dd className="font-medium tabular-nums">{t("learn.scoreValue", { score: review.rawScore, max: review.maxScore })}</dd>
              </div>
              {review.penaltyPercent > 0 && (
                <div className="flex justify-between">
                  <dt className="text-muted-foreground">{t("learn.penalty")}</dt>
                  <dd className="tabular-nums">{t("learn.penaltyValue", { percent: review.penaltyPercent })}</dd>
                </div>
              )}
              <div className="flex justify-between">
                <dt className="text-muted-foreground">{t("grade.final")}</dt>
                <dd className="font-semibold tabular-nums">{review.score !== null ? t("learn.scoreValue", { score: review.score, max: review.maxScore }) : "—"}</dd>
              </div>
              <div className="flex justify-between">
                <dt className="text-muted-foreground">{t("grade.pending")}</dt>
                <dd className="tabular-nums">{review.pendingItems}</dd>
              </div>
            </dl>
            {review.skills.length > 1 && (
              <ul className="mt-4 space-y-1 border-t pt-3 text-sm">
                {review.skills.map((s) => (
                  <li key={s.skillCode} className="flex justify-between">
                    <span className="text-muted-foreground">{s.skillCode ? (skills[s.skillCode] ?? s.skillCode) : t("learn.general")}</span>
                    <span className="tabular-nums">{t("learn.scoreValue", { score: s.score, max: s.maxScore })}</span>
                  </li>
                ))}
              </ul>
            )}
          </Panel>

          {submitted && (
            <Panel title={review.status === "Graded" ? t("grade.gradedTitle") : t("grade.finishTitle")}>
              <form onSubmit={finalize} className="space-y-3">
                <Field label={t("grade.overallComment")}>
                  <Textarea name="comment" rows={4} defaultValue={review.comment ?? ""} maxLength={4000} />
                </Field>
                {error && <FormMessage>{error}</FormMessage>}
                <Button type="submit" className="w-full" disabled={pending || review.pendingItems > 0}>
                  <CheckCircle2 />
                  {review.status === "Graded" ? t("grade.updateComment") : t("grade.finish")}
                </Button>
                {review.pendingItems > 0 && <p className="text-xs text-muted-foreground">{t("grade.finishHint")}</p>}
              </form>
            </Panel>
          )}

          <Panel title={t("grade.activity")} description={t("grade.activityHint")}>
            {review.events.length === 0 ? (
              <p className="text-sm text-muted-foreground">{t("grade.noActivity")}</p>
            ) : (
              <ul className="space-y-1.5 text-sm">
                {review.events.map((e, i) => (
                  <li key={i} className="flex items-start gap-2">
                    {e.kind === "TabHidden" ? <EyeOff className="mt-0.5 size-4 text-muted-foreground" /> : <ClipboardPaste className="mt-0.5 size-4 text-muted-foreground" />}
                    <span>
                      {e.kind === "TabHidden" ? t("grade.tabHidden", { seconds: e.value }) : t("grade.pasted", { characters: e.value })}
                      <span className="block text-xs text-muted-foreground">
                        {format.dateTime(new Date(e.occurredAt), { hour: "2-digit", minute: "2-digit", second: "2-digit" })}
                        {e.itemId && <> · {t("grade.atQuestion", { number: review.items.findIndex((x) => x.id === e.itemId) + 1 })}</>}
                      </span>
                    </span>
                  </li>
                ))}
              </ul>
            )}
          </Panel>
        </div>
      </div>
    </div>
  );
}

function ItemCard({ item, index, total, attemptId, submitted, flagged, onSaved, onError }: {
  item: ResultItem;
  index: number;
  total: number;
  attemptId: string;
  submitted: boolean;
  flagged: boolean;
  onSaved: (review: AttemptReview) => void;
  onError: (code: string) => void;
}) {
  const t = useTranslations("assessment");
  const needs = item.state === "NeedsTeacher";
  const manual = item.type === "Essay" || item.type === "ShortAnswer";
  const [open, setOpen] = useState(needs);
  const [pending, setPending] = useState(false);

  async function save(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = new FormData(event.currentTarget);
    const comment = String(form.get("comment") ?? "");
    const body = item.rubric
      ? { bands: Object.fromEntries(item.rubric.criteria.map((c) => [c.id, Number(form.get(`band-${c.id}`))])), comment }
      : { score: Number(form.get("score")), comment };
    setPending(true);
    const result = await callApi<AttemptReview>(`/teach/api/assessment/attempts/${attemptId}/items/${item.id}/grade`, "PUT", body);
    setPending(false);
    if (result.ok) {
      onSaved(result.data);
      setOpen(false);
    } else onError(result.code);
  }

  return (
    <article className={cn("space-y-3 rounded-xl border bg-card p-4", needs && "border-warning/60")}>
      <header className="flex flex-wrap items-center justify-between gap-2">
        <p className="text-xs font-medium text-muted-foreground">
          {t("player.question", { number: index + 1, total })} · {t(`types.${item.type}`)} · {t("grade.version", { version: item.questionVersion })}
        </p>
        <div className="flex items-center gap-2">
          {flagged && <StatusBadge tone="warning">{t("grade.activityHere")}</StatusBadge>}
          {needs ? (
            <StatusBadge tone="warning">{t("grade.needsGrading")}</StatusBadge>
          ) : (
            <span className="text-sm font-medium tabular-nums">{item.score !== null ? t("learn.scoreValue", { score: item.score, max: item.points }) : "—"}</span>
          )}
        </div>
      </header>
      {item.passage && <RichHtml html={item.passage} className="max-h-40 overflow-y-auto rounded-lg bg-muted/40 px-3 py-2 text-sm" />}
      {item.type !== "FillBlank" && <RichHtml html={item.stem} />}
      <AnswerReview item={item} />
      {item.type === "Essay" && (
        <p className="text-xs text-muted-foreground">
          {t(item.essay?.count === "Characters" ? "grade.characters" : "grade.words", { count: countText(item.response.text, item.essay?.count ?? "Words") })}
        </p>
      )}
      {item.comment && !open && <p className="whitespace-pre-wrap text-sm text-muted-foreground">{t("learn.itemComment", { comment: item.comment })}</p>}

      {submitted && (open ? (
        <form onSubmit={save} className="space-y-3 rounded-lg border bg-muted/30 p-3">
          {item.rubric ? (
            <div className="grid gap-3 sm:grid-cols-2">
              {item.rubric.criteria.map((c) => (
                <Field key={c.id} label={t("grade.band", { name: c.name, max: c.maxBand })}>
                  <NativeSelect name={`band-${c.id}`} defaultValue={String(item.bands?.[c.id] ?? "")} required>
                    <option value="" disabled>{t("grade.pickBand")}</option>
                    {Array.from({ length: c.maxBand * 2 + 1 }, (_, i) => i / 2).map((band) => (
                      <option key={band} value={band}>{band}</option>
                    ))}
                  </NativeSelect>
                </Field>
              ))}
            </div>
          ) : (
            <Field label={t("grade.score", { max: item.points })}>
              <Input type="number" name="score" min={0} max={item.points} step={0.25} defaultValue={item.score ?? ""} required className="w-32" />
            </Field>
          )}
          <Field label={t("grade.comment")}>
            <Textarea name="comment" rows={3} defaultValue={item.comment ?? ""} maxLength={4000} />
          </Field>
          <div className="flex gap-2">
            <Button type="submit" size="sm" disabled={pending}>
              <Save />
              {t("grade.save")}
            </Button>
            {!needs && (
              <Button type="button" size="sm" variant="ghost" onClick={() => setOpen(false)}>{t("player.cancel")}</Button>
            )}
          </div>
        </form>
      ) : (
        <Button type="button" size="sm" variant={manual ? "outline" : "ghost"} onClick={() => setOpen(true)}>
          {t("grade.edit")}
        </Button>
      ))}
    </article>
  );
}
