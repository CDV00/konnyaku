"use client";

import { useTranslations } from "next-intl";
import { ArrowRight, Check, X } from "lucide-react";
import { cn } from "@konnyaku/ui/lib/utils";
import type { AnswerKey, ChoiceOption, ItemResponse, ResultItem } from "./types";
import { splitBlanks } from "./rich-text";

/**
 * Sanitized rich text from the API (allow-listed tags only, no attributes). `hideRuby` hides furigana / pinyin
 * (`<rt>`) for papers that test reading the characters themselves.
 */
export function RichHtml({ html, hideRuby, className, inline }: { html: string | null | undefined; hideRuby?: boolean; className?: string; inline?: boolean }) {
  const Tag = inline ? "span" : "div";
  return (
    <Tag
      className={cn("leading-relaxed break-words [&_ruby]:mx-px [&_rt]:text-[0.6em] [&_rt]:text-muted-foreground", hideRuby && "[&_rt]:hidden", className)}
      dangerouslySetInnerHTML={{ __html: html ?? "" }}
    />
  );
}

/** Option label "A", "B", … from its position. */
export const letter = (index: number) => String.fromCharCode(65 + index);

function Mark({ ok }: { ok: boolean | null }) {
  if (ok === null) return null;
  return ok ? <Check className="size-4 shrink-0 text-success" aria-hidden="true" /> : <X className="size-4 shrink-0 text-destructive" aria-hidden="true" />;
}

function optionLabel(options: ChoiceOption[], id: string | undefined, t: (key: "trueFalse.true" | "trueFalse.false") => string) {
  if (id === "true" || id === "false") return t(`trueFalse.${id}`);
  const index = options.findIndex((o) => o.id === id);
  return index < 0 ? "—" : letter(index);
}

/**
 * What the learner answered for one item, with ticks against the key when the key may be shown (results with answers
 * released, the teacher's review).
 */
export function AnswerReview({ item, hideRuby }: { item: ResultItem; hideRuby?: boolean }) {
  const t = useTranslations("assessment");
  const key: AnswerKey | null = item.key;
  const r: ItemResponse = item.response;
  const label = (id: string | undefined) => optionLabel(item.options, id, t);

  switch (item.type) {
    case "SingleChoice":
    case "MultipleChoice":
    case "TrueFalse": {
      const options = item.type === "TrueFalse" ? [{ id: "true", html: "" }, { id: "false", html: "" }] : item.options;
      return (
        <ul className="space-y-1.5">
          {options.map((o, i) => {
            const chosen = r.choices.includes(o.id);
            const correct = key ? key.choices.includes(o.id) : null;
            return (
              <li
                key={o.id}
                className={cn(
                  "flex items-start gap-2 rounded-lg border px-3 py-2 text-sm",
                  chosen && "border-primary bg-primary/5",
                  correct && "border-success/50 bg-success/10",
                )}
              >
                <span className="font-medium tabular-nums">{item.type === "TrueFalse" ? label(o.id) : letter(i)}</span>
                {item.type !== "TrueFalse" && <RichHtml html={o.html} hideRuby={hideRuby} className="flex-1" />}
                {chosen && <span className="ms-auto text-xs text-muted-foreground">{t("review.chosen")}</span>}
                {key && chosen && <Mark ok={!!correct} />}
              </li>
            );
          })}
        </ul>
      );
    }
    case "FillBlank": {
      const { parts, blanks } = splitBlanks(item.stem);
      return (
        <div className="space-y-2 text-sm">
          <p className="leading-loose">
            {parts.map((part, i) => (
              <span key={i}>
                <RichHtml html={part} hideRuby={hideRuby} inline />
                {i < blanks.length && (
                  <span className="mx-1 inline-flex items-center gap-1 rounded-md border bg-muted/50 px-2 py-0.5 font-medium">
                    {r.blanks[blanks[i] ?? 0] || "—"}
                  </span>
                )}
              </span>
            ))}
          </p>
          {key && (
            <ul className="space-y-1 text-muted-foreground">
              {key.blanks.map((accepted, i) => (
                <li key={i}>{t("review.blankKey", { number: i + 1, answers: accepted.join(" / ") })}</li>
              ))}
            </ul>
          )}
        </div>
      );
    }
    case "Matching":
      return (
        <ul className="space-y-1.5 text-sm">
          {item.options.map((o) => {
            const target = item.targets.find((x) => x.id === r.pairs[o.id]);
            const ok = key ? key.pairs[o.id] === r.pairs[o.id] : null;
            const right = key ? item.targets.find((x) => x.id === key.pairs[o.id]) : undefined;
            return (
              <li key={o.id} className="flex flex-wrap items-center gap-2 rounded-lg border px-3 py-2">
                <RichHtml html={o.html} hideRuby={hideRuby} inline />
                <ArrowRight className="size-3.5 text-muted-foreground" aria-hidden="true" />
                {target ? <RichHtml html={target.html} hideRuby={hideRuby} inline /> : <span>—</span>}
                <Mark ok={ok} />
                {key && !ok && right && (
                  <span className="text-xs text-muted-foreground">
                    {t("review.correctIs")} <RichHtml html={right.html} hideRuby={hideRuby} inline />
                  </span>
                )}
              </li>
            );
          })}
        </ul>
      );
    case "Ordering": {
      const order = r.order.length > 0 ? r.order : [];
      const byId = new Map(item.options.map((o) => [o.id, o]));
      return (
        <div className="space-y-2 text-sm">
          {order.length === 0 ? (
            <p className="text-muted-foreground">{t("review.noAnswer")}</p>
          ) : (
            <ol className="flex flex-wrap gap-1.5">
              {order.map((id, i) => (
                <li key={id} className="flex items-center gap-1 rounded-md border px-2 py-1">
                  <span className="text-xs text-muted-foreground tabular-nums">{i + 1}</span>
                  <RichHtml html={byId.get(id)?.html} hideRuby={hideRuby} inline />
                </li>
              ))}
            </ol>
          )}
          {key && (
            <p className="text-muted-foreground">
              {t("review.correctOrder")}{" "}
              {key.order.map((id, i) => (
                <span key={id}>
                  {i > 0 && " / "}
                  <RichHtml html={byId.get(id)?.html} hideRuby={hideRuby} inline />
                </span>
              ))}
            </p>
          )}
        </div>
      );
    }
    default:
      return (
        <div className="space-y-2 text-sm">
          <p className="whitespace-pre-wrap rounded-lg border bg-muted/40 px-3 py-2">{r.text?.trim() ? r.text : t("review.noAnswer")}</p>
          {key && item.type === "ShortAnswer" && key.accepted.length > 0 && (
            <p className="text-muted-foreground">{t("review.accepted", { answers: key.accepted.join(" / ") })}</p>
          )}
        </div>
      );
  }
}
