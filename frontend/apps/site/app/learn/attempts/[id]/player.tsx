"use client";

import { useCallback, useEffect, useMemo, useRef, useState, type ClipboardEvent } from "react";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { ArrowDown, ArrowLeft, ArrowRight, ArrowUp, BookOpen, CheckCircle2, Clock, Cloud, CloudOff, Flag, Lock, Send } from "lucide-react";
import { Button } from "@konnyaku/ui/components/animate-ui/components/buttons/button";
import { Input } from "@konnyaku/ui/components/ui/input";
import { Textarea } from "@konnyaku/ui/components/ui/textarea";
import { FormMessage } from "@konnyaku/ui/kk/blocks";
import { NativeSelect } from "@konnyaku/ui/kk/field";
import { cn } from "@konnyaku/ui/lib/utils";
import { callApi } from "@/assessment/client";
import { RichHtml, letter } from "@/assessment/render";
import { countText, plainText, splitBlanks } from "@/assessment/rich-text";
import { emptyResponse, errorKey, type AttemptPlay, type ItemResponse, type PlayItem } from "@/assessment/types";

const AUTOSAVE_MS = 5000;
const base = (id: string) => `/learn/api/assessment/attempts/${id}`;

function answered(item: PlayItem, r: ItemResponse): boolean {
  switch (item.type) {
    case "FillBlank":
      return r.blanks.some((b) => b?.trim());
    case "Matching":
      return Object.keys(r.pairs).length > 0;
    case "Ordering":
      return r.order.length > 0;
    case "ShortAnswer":
    case "Essay":
      return !!r.text?.trim();
    default:
      return r.choices.length > 0;
  }
}

/** Remaining milliseconds until `deadline` on the server's clock. */
function useRemaining(deadline: string | null, offset: number) {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, []);
  return deadline ? Date.parse(deadline) - (now + offset) : null;
}

function clock(ms: number) {
  const total = Math.max(0, Math.floor(ms / 1000));
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  const pad = (n: number) => String(n).padStart(2, "0");
  return h > 0 ? `${h}:${pad(m)}:${pad(s)}` : `${pad(m)}:${pad(s)}`;
}

export function Player({ initial }: { initial: AttemptPlay }) {
  const t = useTranslations("assessment");
  const router = useRouter();
  const [attempt, setAttempt] = useState(initial);
  const [offset, setOffset] = useState(() => Date.parse(initial.serverNow) - Date.now());
  const [responses, setResponses] = useState<Record<string, ItemResponse>>(() =>
    Object.fromEntries(initial.items.map((i) => [i.id, { ...emptyResponse(), ...i.response }])),
  );
  const [flags, setFlags] = useState<Record<string, boolean>>(() => Object.fromEntries(initial.items.map((i) => [i.id, i.flagged])));
  const [index, setIndex] = useState(() => Math.max(0, initial.items.findIndex((i) => !i.locked && i.sectionIndex === initial.sectionIndex)));
  const [saving, setSaving] = useState<"saved" | "saving" | "offline">("saved");
  const [error, setError] = useState<string | null>(null);
  const [confirming, setConfirming] = useState<"submit" | "section" | null>(null);
  const [busy, setBusy] = useState(false);
  const dirty = useRef(new Set<string>());
  const hiddenAt = useRef<number | null>(null);

  const items = attempt.items;
  const item = items[index];
  const remaining = useRemaining(attempt.deadlineAt, offset);
  const sectionRemaining = useRemaining(attempt.timed ? attempt.sectionDeadlineAt : null, offset);
  const current = useMemo(() => items.filter((i) => !i.locked && (!attempt.timed || i.sectionIndex === attempt.sectionIndex)), [items, attempt]);
  const unanswered = current.filter((i) => !answered(i, responses[i.id] ?? emptyResponse())).length;
  const lastSection = !attempt.timed || attempt.sectionIndex >= attempt.sections.length - 1;

  const showError = useCallback((code: string) => {
    const key = `errors.${errorKey(code)}`;
    setError(t.has(key) ? t(key as "errors.unknown") : t("errors.unknown"));
  }, [t]);

  const goToResult = useCallback(() => router.replace(`/learn/attempts/${attempt.id}/result`), [router, attempt.id]);

  const save = useCallback(async () => {
    if (dirty.current.size === 0) return true;
    const ids = [...dirty.current];
    dirty.current.clear();
    setSaving("saving");
    const result = await callApi<{ serverNow: string }>(`${base(attempt.id)}/answers`, "PUT", {
      answers: ids.map((id) => ({ itemId: id, response: responses[id], flagged: !!flags[id] })),
    });
    if (result.ok) {
      setOffset(Date.parse(result.data.serverNow) - Date.now());
      setSaving("saved");
      return true;
    }
    ids.forEach((id) => dirty.current.add(id));
    if (result.code === "assessment.attempt_closed") {
      goToResult();
      return false;
    }
    if (result.code === "assessment.section_closed") {
      await reload();
      return false;
    }
    setSaving("offline");
    return false;
    // reload is stable enough for this purpose; listing it would re-create save on every attempt change.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [attempt.id, responses, flags, goToResult]);

  async function reload() {
    const result = await callApi<AttemptPlay>(base(attempt.id));
    if (!result.ok) return;
    if (result.data.status !== "InProgress") {
      goToResult();
      return;
    }
    setAttempt(result.data);
    setOffset(Date.parse(result.data.serverNow) - Date.now());
    setIndex(Math.max(0, result.data.items.findIndex((i) => !i.locked && i.sectionIndex === result.data.sectionIndex)));
  }

  // Autosave every few seconds while something changed.
  useEffect(() => {
    const timer = setInterval(() => void save(), AUTOSAVE_MS);
    return () => clearInterval(timer);
  }, [save]);

  // Tab hidden: save now, and tell the teacher how long the tab was away (logged, never blocking).
  useEffect(() => {
    function onVisibility() {
      if (document.visibilityState === "hidden") {
        hiddenAt.current = Date.now();
        void save();
      } else if (hiddenAt.current) {
        const seconds = Math.round((Date.now() - hiddenAt.current) / 1000);
        hiddenAt.current = null;
        if (seconds >= 2) void callApi(`${base(attempt.id)}/events`, "POST", { kind: "TabHidden", itemId: item?.id, value: seconds });
      }
    }
    document.addEventListener("visibilitychange", onVisibility);
    return () => document.removeEventListener("visibilitychange", onVisibility);
  }, [save, attempt.id, item?.id]);

  // The server hands the attempt in at the deadline; follow it there. A timed section that ran out unlocks the next one.
  useEffect(() => {
    if (remaining !== null && remaining <= 0) {
      void save().finally(goToResult);
    } else if (sectionRemaining !== null && sectionRemaining <= 0 && !lastSection) {
      void save().then(reload);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [remaining !== null && remaining <= 0, sectionRemaining !== null && sectionRemaining <= 0]);

  function update(id: string, change: Partial<ItemResponse>) {
    setResponses((all) => ({ ...all, [id]: { ...(all[id] ?? emptyResponse()), ...change } }));
    dirty.current.add(id);
  }

  function toggleFlag(id: string) {
    setFlags((all) => ({ ...all, [id]: !all[id] }));
    dirty.current.add(id);
  }

  function onPaste(event: ClipboardEvent) {
    const length = event.clipboardData.getData("text").length;
    if (length > 0) void callApi(`${base(attempt.id)}/events`, "POST", { kind: "Paste", itemId: item?.id, value: length });
  }

  async function submit() {
    setBusy(true);
    await save();
    const result = await callApi(`${base(attempt.id)}/submit`, "POST");
    setBusy(false);
    if (result.ok || result.code === "assessment.attempt_closed") goToResult();
    else showError(result.code);
  }

  async function nextSection() {
    setBusy(true);
    await save();
    const result = await callApi<AttemptPlay>(`${base(attempt.id)}/next-section`, "POST");
    setBusy(false);
    setConfirming(null);
    if (!result.ok) {
      showError(result.code);
      return;
    }
    setAttempt(result.data);
    setIndex(Math.max(0, result.data.items.findIndex((i) => !i.locked && i.sectionIndex === result.data.sectionIndex)));
  }

  function move(step: number) {
    let next = index + step;
    while (next >= 0 && next < items.length && items[next]?.locked) next += step;
    if (next >= 0 && next < items.length) setIndex(next);
  }

  if (!item) return null;
  const response = responses[item.id] ?? emptyResponse();
  const readOnly = item.locked || (attempt.timed && item.sectionIndex !== attempt.sectionIndex);
  const low = remaining !== null && remaining < 60_000;

  return (
    <div className="space-y-4 pb-24" onPaste={onPaste}>
      {/* Header: title, timer, save state */}
      <div className="sticky top-14 z-10 -mx-4 flex items-center justify-between gap-3 border-b bg-background/95 px-4 py-2 backdrop-blur sm:mx-0 sm:rounded-xl sm:border">
        <div className="min-w-0">
          <p className="truncate text-sm font-semibold">{attempt.title}</p>
          {attempt.timed && (
            <p className="truncate text-xs text-muted-foreground">
              {t("player.section", { number: attempt.sectionIndex + 1, total: attempt.sections.length, title: attempt.sections[attempt.sectionIndex]?.title ?? "" })}
            </p>
          )}
        </div>
        <div className="flex shrink-0 items-center gap-3">
          <span className="flex items-center gap-1 text-xs text-muted-foreground" role="status">
            {saving === "offline" ? <CloudOff className="size-4 text-warning" /> : <Cloud className="size-4" />}
            <span className="hidden sm:inline">{t(`player.save.${saving}`)}</span>
          </span>
          {(sectionRemaining ?? remaining) !== null && (
            <span
              className={cn("flex items-center gap-1 rounded-md px-2 py-1 font-mono text-sm tabular-nums", low ? "bg-destructive/10 text-destructive" : "bg-muted")}
              aria-label={t("player.timeLeft")}
            >
              <Clock className="size-4" />
              {clock((attempt.timed ? sectionRemaining : remaining) ?? 0)}
            </span>
          )}
        </div>
      </div>

      {/* Navigator */}
      <nav aria-label={t("player.navigator")} className="flex flex-wrap gap-1.5">
        {items.map((it, i) => {
          const done = answered(it, responses[it.id] ?? emptyResponse());
          return (
            <button
              key={it.id}
              type="button"
              disabled={it.locked}
              onClick={() => setIndex(i)}
              aria-current={i === index ? "step" : undefined}
              aria-label={t("player.goTo", { number: i + 1 })}
              className={cn(
                "relative grid size-9 place-items-center rounded-md border text-sm tabular-nums transition-colors",
                done && "border-primary/40 bg-primary/10",
                i === index && "ring-2 ring-primary",
                it.locked && "cursor-not-allowed opacity-40",
              )}
            >
              {it.locked ? <Lock className="size-3.5" /> : i + 1}
              {flags[it.id] && <span className="absolute -right-1 -top-1 size-2.5 rounded-full bg-warning" aria-hidden="true" />}
            </button>
          );
        })}
      </nav>

      {/* Passage of a reading group */}
      {item.passage && (
        <details open className="rounded-xl border bg-card">
          <summary className="flex cursor-pointer items-center gap-2 px-4 py-3 text-sm font-medium">
            <BookOpen className="size-4" />
            {item.groupTitle || t("player.passage")}
          </summary>
          <RichHtml html={item.passage} hideRuby={!attempt.showRuby} className="max-h-[50vh] overflow-y-auto border-t px-4 py-3" />
        </details>
      )}

      {/* The question */}
      <section className="space-y-4 rounded-xl border bg-card p-4 sm:p-6" aria-labelledby="question-title">
        <div className="flex items-start justify-between gap-3">
          <p id="question-title" className="text-xs font-medium text-muted-foreground">
            {t("player.question", { number: index + 1, total: items.length })} · {t(`types.${item.type}`)} · {t("player.points", { points: item.points })}
          </p>
          <Button
            type="button"
            variant={flags[item.id] ? "secondary" : "ghost"}
            size="sm"
            onClick={() => toggleFlag(item.id)}
            disabled={readOnly}
            aria-pressed={!!flags[item.id]}
            aria-label={t("player.flag")}
          >
            <Flag className={cn("size-4", flags[item.id] && "fill-warning text-warning")} />
            <span className="hidden sm:inline">{t("player.flag")}</span>
          </Button>
        </div>

        {item.type !== "FillBlank" && <RichHtml html={item.stem} hideRuby={!attempt.showRuby} className="text-base" />}
        <ItemInput item={item} response={response} readOnly={readOnly} hideRuby={!attempt.showRuby} onChange={(c) => update(item.id, c)} />
      </section>

      {error && <FormMessage>{error}</FormMessage>}

      {/* Footer: previous / next / submit */}
      <div className="fixed inset-x-0 bottom-0 z-10 border-t bg-background/95 px-4 py-3 backdrop-blur">
        <div className="mx-auto flex max-w-6xl items-center justify-between gap-2">
          <Button type="button" variant="outline" onClick={() => move(-1)} disabled={index === 0} aria-label={t("player.previous")}>
            <ArrowLeft />
            <span className="hidden sm:inline">{t("player.previous")}</span>
          </Button>
          {confirming ? (
            <div className="flex flex-1 flex-wrap items-center justify-end gap-2 text-sm">
              <span className="text-muted-foreground">
                {confirming === "submit" ? t("player.confirmSubmit", { unanswered }) : t("player.confirmSection", { unanswered })}
              </span>
              <Button type="button" variant="ghost" onClick={() => setConfirming(null)} disabled={busy}>{t("player.cancel")}</Button>
              <Button type="button" onClick={() => void (confirming === "submit" ? submit() : nextSection())} disabled={busy}>
                <CheckCircle2 />
                {t("player.confirm")}
              </Button>
            </div>
          ) : (
            <div className="flex items-center gap-2">
              {index < items.length - 1 && !items[index + 1]?.locked && (
                <Button type="button" variant="outline" onClick={() => move(1)} aria-label={t("player.next")}>
                  <span className="hidden sm:inline">{t("player.next")}</span>
                  <ArrowRight />
                </Button>
              )}
              {lastSection ? (
                <Button type="button" onClick={() => setConfirming("submit")}>
                  <Send />
                  {t("player.submit")}
                </Button>
              ) : (
                <Button type="button" variant="secondary" onClick={() => setConfirming("section")}>
                  {t("player.nextSection")}
                  <ArrowRight />
                </Button>
              )}
            </div>
          )}
        </div>
      </div>
    </div>
  );
}

function ItemInput({ item, response, readOnly, hideRuby, onChange }: {
  item: PlayItem;
  response: ItemResponse;
  readOnly: boolean;
  hideRuby: boolean;
  onChange: (change: Partial<ItemResponse>) => void;
}) {
  const t = useTranslations("assessment");

  switch (item.type) {
    case "SingleChoice":
    case "MultipleChoice":
    case "TrueFalse": {
      const multiple = item.type === "MultipleChoice";
      return (
        <fieldset className="space-y-2" disabled={readOnly}>
          <legend className="sr-only">{t("player.choose")}</legend>
          {multiple && <p className="text-xs text-muted-foreground">{t("player.chooseMany")}</p>}
          {item.options.map((o, i) => {
            const chosen = response.choices.includes(o.id);
            return (
              <label
                key={o.id}
                className={cn(
                  "flex min-h-12 cursor-pointer items-center gap-3 rounded-lg border px-3 py-2 transition-colors hover:bg-accent",
                  chosen && "border-primary bg-primary/5",
                )}
              >
                <input
                  type={multiple ? "checkbox" : "radio"}
                  name={item.id}
                  className="size-4 accent-[var(--color-primary)]"
                  checked={chosen}
                  onChange={() =>
                    onChange({ choices: multiple ? (chosen ? response.choices.filter((c) => c !== o.id) : [...response.choices, o.id]) : [o.id] })
                  }
                />
                {item.type === "TrueFalse" ? (
                  <span>{t(`trueFalse.${o.id as "true" | "false"}`)}</span>
                ) : (
                  <>
                    <span className="font-medium tabular-nums">{letter(i)}</span>
                    <RichHtml html={o.html} hideRuby={hideRuby} className="flex-1" />
                  </>
                )}
              </label>
            );
          })}
        </fieldset>
      );
    }
    case "FillBlank": {
      const { parts, blanks } = splitBlanks(item.stem);
      return (
        <p className="text-base leading-loose">
          {parts.map((part, i) => {
            const blank = blanks[i] ?? 0;
            return (
            <span key={i}>
              <RichHtml html={part} hideRuby={hideRuby} inline />
              {i < blanks.length && (
                <Input
                  aria-label={t("player.blank", { number: blank + 1 })}
                  className="mx-1 inline-flex h-8 w-36 align-baseline"
                  disabled={readOnly}
                  value={response.blanks[blank] ?? ""}
                  autoComplete="off"
                  autoCapitalize="off"
                  spellCheck={false}
                  onChange={(e) => {
                    const next = [...response.blanks];
                    while (next.length < item.blanks) next.push("");
                    next[blank] = e.target.value;
                    onChange({ blanks: next });
                  }}
                />
              )}
            </span>
            );
          })}
        </p>
      );
    }
    case "Matching":
      return (
        <ul className="space-y-2">
          {item.options.map((o) => (
            <li key={o.id} className="grid gap-2 rounded-lg border p-3 sm:grid-cols-[1fr_minmax(0,16rem)] sm:items-center">
              <RichHtml html={o.html} hideRuby={hideRuby} />
              <NativeSelect
                aria-label={t("player.matchFor")}
                disabled={readOnly}
                value={response.pairs[o.id] ?? ""}
                onChange={(e) => {
                  const pairs = { ...response.pairs };
                  if (e.target.value) pairs[o.id] = e.target.value;
                  else delete pairs[o.id];
                  onChange({ pairs });
                }}
              >
                <option value="">{t("player.pick")}</option>
                {item.targets.map((target, i) => {
                  const label = `${letter(i)}. ${plainText(target.html)}`;
                  return <option key={target.id} value={target.id}>{label}</option>;
                })}
              </NativeSelect>
            </li>
          ))}
        </ul>
      );
    case "Ordering": {
      const order = response.order.length === item.options.length ? response.order : item.options.map((o) => o.id);
      const byId = new Map(item.options.map((o) => [o.id, o]));
      const swap = (i: number, j: number) => {
        const next = [...order];
        const a = next[i];
        const b = next[j];
        if (a === undefined || b === undefined) return;
        next[i] = b;
        next[j] = a;
        onChange({ order: next });
      };
      return (
        <div className="space-y-2">
          <p className="text-xs text-muted-foreground">{t("player.orderHint")}</p>
          <ol className="space-y-2">
            {order.map((id, i) => (
              <li key={id} className="flex items-center gap-2 rounded-lg border px-3 py-2">
                <span className="w-5 text-sm text-muted-foreground tabular-nums">{i + 1}</span>
                <RichHtml html={byId.get(id)?.html} hideRuby={hideRuby} className="flex-1" />
                <Button type="button" size="icon" variant="ghost" disabled={readOnly || i === 0} onClick={() => swap(i, i - 1)} aria-label={t("player.moveUp")}>
                  <ArrowUp />
                </Button>
                <Button type="button" size="icon" variant="ghost" disabled={readOnly || i === order.length - 1} onClick={() => swap(i, i + 1)} aria-label={t("player.moveDown")}>
                  <ArrowDown />
                </Button>
              </li>
            ))}
          </ol>
        </div>
      );
    }
    case "ShortAnswer":
      return (
        <Input
          aria-label={t("player.yourAnswer")}
          disabled={readOnly}
          value={response.text ?? ""}
          autoComplete="off"
          onChange={(e) => onChange({ text: e.target.value })}
        />
      );
    case "Essay": {
      const limits = item.essay;
      const mode = limits?.count ?? "Words";
      const count = countText(response.text, mode);
      const outside = (limits?.min != null && count < limits.min) || (limits?.max != null && count > limits.max);
      return (
        <div className="space-y-1.5">
          <Textarea
            aria-label={t("player.yourAnswer")}
            disabled={readOnly}
            rows={10}
            value={response.text ?? ""}
            onChange={(e) => onChange({ text: e.target.value })}
          />
          <p className={cn("text-xs tabular-nums", outside ? "text-warning-foreground dark:text-warning" : "text-muted-foreground")}>
            {t(mode === "Characters" ? "player.characters" : "player.words", { count, min: limits?.min ?? 0, max: limits?.max ?? 0 })}
          </p>
        </div>
      );
    }
    default:
      return null;
  }
}
