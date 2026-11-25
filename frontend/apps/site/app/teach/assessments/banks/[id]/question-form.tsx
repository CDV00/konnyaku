"use client";

import { useState, type FormEvent } from "react";
import { useLocale, useTranslations } from "next-intl";
import { ArrowDown, ArrowUp, Plus, Trash2 } from "lucide-react";
import { Button } from "@konnyaku/ui/components/animate-ui/components/buttons/button";
import { Input } from "@konnyaku/ui/components/ui/input";
import { Textarea } from "@konnyaku/ui/components/ui/textarea";
import { FormMessage } from "@konnyaku/ui/kk/blocks";
import { CheckRow, Field, NativeSelect } from "@konnyaku/ui/kk/field";
import { callApi } from "@/assessment/client";
import { fromHtml, plainText, toHtml } from "@/assessment/rich-text";
import { questionTypes, type AnswerKey, type Framework, type Group, type Question, type QuestionContent, type QuestionType } from "@/assessment/types";
import { localizedName, newId, splitList, useErrorText } from "../../authoring";
import { RichTextInput } from "./rich-text-input";

type Line = { id: string; text: string };

type Draft = {
  type: QuestionType;
  stem: string;
  options: Line[];
  targets: Line[];
  choices: string[];
  pairs: Record<string, string>;
  blanks: string[];
  accepted: string;
  ignoreCase: boolean;
  ignoreDiacritics: boolean;
  essayMin: string;
  essayMax: string;
  essayCount: "Words" | "Characters";
  shuffle: boolean;
  points: string;
  frameworkCode: string;
  skillCode: string;
  levelCode: string;
  tags: string;
  explanation: string;
  groupId: string;
};

const maxOptions = 12;
const characterLanguages = ["ja", "zh"];

/** Blank numbers written `[[n]]` in the stem: the highest number is the blank count (the API wants 1..n, no gaps). */
function blankNumbers(stem: string): number[] {
  return [...new Set([...stem.matchAll(/\[\[(\d{1,2})\]\]/g)].map((m) => Number(m[1])))].sort((a, b) => a - b);
}

function toDraft(q: Question | null, languageCode: string, frameworks: Framework[], groupId: string | null): Draft {
  const lines = (items: { id: string; html: string }[] | undefined) => (items ?? []).map((o) => ({ id: o.id, text: fromHtml(o.html) }));
  if (!q) {
    const a = newId("o");
    const b = newId("o", [a]);
    return {
      type: "SingleChoice",
      stem: "",
      options: [
        { id: a, text: "" },
        { id: b, text: "" },
      ],
      targets: [
        { id: newId("t"), text: "" },
        { id: newId("t"), text: "" },
      ],
      choices: [],
      pairs: {},
      blanks: [],
      accepted: "",
      ignoreCase: true,
      ignoreDiacritics: false,
      essayMin: "",
      essayMax: "",
      essayCount: characterLanguages.includes(languageCode) ? "Characters" : "Words",
      shuffle: true,
      points: "1",
      frameworkCode: frameworks.length === 1 ? frameworks[0]!.code : "",
      skillCode: "",
      levelCode: "",
      tags: "",
      explanation: "",
      groupId: groupId ?? "",
    };
  }
  // Ordering: show the options in the correct order (the API shuffles them per attempt anyway).
  const options = lines(q.content.options);
  const ordered =
    q.type === "Ordering" && q.key.order.length === options.length
      ? q.key.order.map((id) => options.find((o) => o.id === id)).filter((o): o is Line => !!o)
      : options;
  return {
    type: q.type,
    stem: fromHtml(q.stem),
    options: q.type === "TrueFalse" ? [] : ordered,
    targets: lines(q.content.targets),
    choices: q.key.choices,
    pairs: q.key.pairs ?? {},
    blanks: q.key.blanks.map((b) => b.join("\n")),
    accepted: q.key.accepted.join("\n"),
    ignoreCase: q.key.ignoreCase,
    ignoreDiacritics: q.key.ignoreDiacritics,
    essayMin: q.content.essay?.min?.toString() ?? "",
    essayMax: q.content.essay?.max?.toString() ?? "",
    essayCount: q.content.essay?.count ?? (characterLanguages.includes(languageCode) ? "Characters" : "Words"),
    shuffle: q.content.shuffle,
    points: String(q.points),
    frameworkCode: q.frameworkCode ?? (frameworks.length === 1 ? frameworks[0]!.code : ""),
    skillCode: q.skillCode ?? "",
    levelCode: q.levelCode ?? "",
    tags: q.tags.join(", "),
    explanation: fromHtml(q.explanation),
    groupId: q.groupId ?? "",
  };
}

const lines = (value: string) =>
  value
    .split(/\r?\n/)
    .map((x) => x.trim())
    .filter((x) => x.length > 0);

const optional = (value: string) => (value.trim() === "" ? null : Number(value));

function toCommand(d: Draft) {
  const usesOptions = ["SingleChoice", "MultipleChoice", "Matching", "Ordering"].includes(d.type);
  const blankCount = d.type === "FillBlank" ? Math.max(0, ...blankNumbers(d.stem)) : 0;
  const key: AnswerKey = {
    choices: ["SingleChoice", "MultipleChoice", "TrueFalse"].includes(d.type) ? d.choices : [],
    order: d.type === "Ordering" ? d.options.map((o) => o.id) : [],
    pairs: d.type === "Matching" ? Object.fromEntries(d.options.map((o) => [o.id, d.pairs[o.id] ?? ""]).filter(([, v]) => v)) : {},
    blanks: d.type === "FillBlank" ? Array.from({ length: blankCount }, (_, i) => lines(d.blanks[i] ?? "")) : [],
    accepted: d.type === "ShortAnswer" ? lines(d.accepted) : [],
    ignoreCase: d.ignoreCase,
    ignoreDiacritics: d.ignoreDiacritics,
  };
  const content: QuestionContent = {
    options: usesOptions ? d.options.map((o) => ({ id: o.id, html: toHtml(o.text) })) : [],
    targets: d.type === "Matching" ? d.targets.map((o) => ({ id: o.id, html: toHtml(o.text) })) : [],
    blanks: blankCount,
    essay: d.type === "Essay" ? { min: optional(d.essayMin), max: optional(d.essayMax), count: d.essayCount } : null,
    shuffle: d.shuffle,
  };
  const skillCode = d.skillCode || null;
  const levelCode = d.levelCode || null;
  return {
    type: d.type,
    stem: toHtml(d.stem),
    content,
    key,
    points: Number(d.points),
    frameworkCode: skillCode || levelCode ? d.frameworkCode || null : null,
    skillCode,
    levelCode,
    tags: splitList(d.tags),
    explanation: d.explanation.trim() ? toHtml(d.explanation) : null,
    groupId: d.groupId || null,
  };
}

/** Create or edit one question (all 8 types). Saving an existing question creates a new version. */
export function QuestionForm({ bankId, question, groups, frameworks, languageCode, defaultGroupId, readOnly, onSaved }: {
  bankId: string;
  question: Question | null;
  groups: Group[];
  frameworks: Framework[];
  languageCode: string;
  defaultGroupId: string | null;
  readOnly: boolean;
  onSaved: (question: Question) => void;
}) {
  const t = useTranslations("assessmentAuthoring");
  const locale = useLocale();
  const errorText = useErrorText();
  const [draft, setDraft] = useState<Draft>(() => toDraft(question, languageCode, frameworks, defaultGroupId));
  const [pending, setPending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const set = (patch: Partial<Draft>) => setDraft((d) => ({ ...d, ...patch }));

  const framework = frameworks.find((f) => f.code === draft.frameworkCode);
  const blanks = blankNumbers(draft.stem);
  const blankCount = blanks.length ? Math.max(...blanks) : 0;
  const blanksContiguous = blanks.length === blankCount;
  const optionLabel = (i: number) => String.fromCharCode(65 + i);

  function changeType(type: QuestionType) {
    setDraft((d) => {
      const next = { ...d, type };
      if (type === "TrueFalse") next.choices = d.choices.length === 1 && ["true", "false"].includes(d.choices[0]!) ? d.choices : ["true"];
      else if (type === "SingleChoice") next.choices = d.choices.filter((c) => d.options.some((o) => o.id === c)).slice(0, 1);
      else next.choices = d.choices.filter((c) => d.options.some((o) => o.id === c));
      if (type === "Essay" && !d.essayMin && !d.essayMax) next.essayCount = characterLanguages.includes(languageCode) ? "Characters" : "Words";
      if (type === "FillBlank" && !d.stem.includes("[[")) next.stem = d.stem ? `${d.stem} [[1]]` : "[[1]]";
      return next;
    });
  }

  function setLine(field: "options" | "targets", id: string, text: string) {
    setDraft((d) => ({ ...d, [field]: d[field].map((o) => (o.id === id ? { ...o, text } : o)) }));
  }

  function addLine(field: "options" | "targets") {
    setDraft((d) => ({ ...d, [field]: [...d[field], { id: newId(field === "options" ? "o" : "t", d[field].map((o) => o.id)), text: "" }] }));
  }

  function removeLine(field: "options" | "targets", id: string) {
    setDraft((d) => ({
      ...d,
      [field]: d[field].filter((o) => o.id !== id),
      choices: d.choices.filter((c) => c !== id),
      pairs: Object.fromEntries(Object.entries(d.pairs).filter(([k, v]) => k !== id && v !== id)),
    }));
  }

  function move(id: string, delta: number) {
    setDraft((d) => {
      const index = d.options.findIndex((o) => o.id === id);
      const target = index + delta;
      if (index < 0 || target < 0 || target >= d.options.length) return d;
      const options = [...d.options];
      [options[index], options[target]] = [options[target]!, options[index]!];
      return { ...d, options };
    });
  }

  function toggleChoice(id: string) {
    setDraft((d) => {
      if (d.type === "SingleChoice") return { ...d, choices: [id] };
      return { ...d, choices: d.choices.includes(id) ? d.choices.filter((c) => c !== id) : [...d.choices, id] };
    });
  }

  async function onSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (readOnly) return;
    setPending(true);
    setError(null);
    const body = toCommand(draft);
    const result = question
      ? await callApi<Question>(`/teach/api/assessment/questions/${question.id}`, "PUT", body)
      : await callApi<Question>(`/teach/api/assessment/banks/${bankId}/questions`, "POST", body);
    setPending(false);
    if (!result.ok) {
      setError(errorText(result.code, result.params));
      return;
    }
    onSaved(result.data);
  }

  const lineEditor = (field: "options" | "targets") => {
    const items = draft[field];
    const isOptions = field === "options";
    return (
      <ol className="space-y-3">
        {items.map((o, i) => (
          <li key={o.id} className="space-y-2 rounded-lg border p-3">
            <div className="flex flex-wrap items-center gap-2">
              <span className="grid size-6 shrink-0 place-items-center rounded-full bg-muted text-xs font-semibold">{isOptions ? optionLabel(i) : i + 1}</span>
              {isOptions && (draft.type === "SingleChoice" || draft.type === "MultipleChoice") && (
                <CheckRow
                  type={draft.type === "SingleChoice" ? "radio" : "checkbox"}
                  name="correct"
                  checked={draft.choices.includes(o.id)}
                  onChange={() => toggleChoice(o.id)}
                >
                  {t("question.correct")}
                </CheckRow>
              )}
              <div className="ml-auto flex items-center gap-1">
                {isOptions && draft.type === "Ordering" && (
                  <>
                    <Button type="button" variant="ghost" size="icon-sm" onClick={() => move(o.id, -1)} disabled={i === 0} aria-label={t("common.moveUp")}>
                      <ArrowUp />
                    </Button>
                    <Button
                      type="button"
                      variant="ghost"
                      size="icon-sm"
                      onClick={() => move(o.id, 1)}
                      disabled={i === items.length - 1}
                      aria-label={t("common.moveDown")}
                    >
                      <ArrowDown />
                    </Button>
                  </>
                )}
                <Button type="button" variant="ghost" size="icon-sm" onClick={() => removeLine(field, o.id)} disabled={items.length <= 2} aria-label={t("common.remove")}>
                  <Trash2 />
                </Button>
              </div>
            </div>
            <RichTextInput
              label={isOptions ? t("question.optionN", { n: optionLabel(i) }) : t("question.targetN", { n: i + 1 })}
              value={o.text}
              onChange={(text) => setLine(field, o.id, text)}
              rows={1}
              compact
              disabled={readOnly}
            />
            {isOptions && draft.type === "Matching" && (
              <Field label={t("question.matchesWith")}>
                <NativeSelect value={draft.pairs[o.id] ?? ""} onChange={(e) => set({ pairs: { ...draft.pairs, [o.id]: e.target.value } })}>
                  <option value="">{t("common.choose")}</option>
                  {draft.targets.map((target, j) => (
                    <option key={target.id} value={target.id}>
                      {t("question.targetOption", { n: j + 1, text: plainText(toHtml(target.text), 60) })}
                    </option>
                  ))}
                </NativeSelect>
              </Field>
            )}
          </li>
        ))}
        {items.length < maxOptions && (
          <li>
            <Button type="button" variant="outline" size="sm" onClick={() => addLine(field)}>
              <Plus />
              {isOptions ? t("question.addOption") : t("question.addTarget")}
            </Button>
          </li>
        )}
      </ol>
    );
  };

  return (
    <form onSubmit={onSubmit} className="space-y-5">
      <fieldset disabled={readOnly || pending} className="space-y-5">
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label={t("question.type")}>
            <NativeSelect value={draft.type} onChange={(e) => changeType(e.target.value as QuestionType)}>
              {questionTypes.map((type) => (
                <option key={type} value={type}>
                  {t(`types.${type}`)}
                </option>
              ))}
            </NativeSelect>
          </Field>
          <Field label={t("question.group")}>
            <NativeSelect value={draft.groupId} onChange={(e) => set({ groupId: e.target.value })}>
              <option value="">{t("question.noGroup")}</option>
              {groups.map((g) => (
                <option key={g.id} value={g.id}>
                  {g.title}
                </option>
              ))}
            </NativeSelect>
          </Field>
        </div>
        <p className="text-xs text-muted-foreground">{t(`typeHints.${draft.type}`)}</p>

        <RichTextInput
          label={t("question.stem")}
          value={draft.stem}
          onChange={(stem) => set({ stem })}
          rows={4}
          hint={draft.type === "FillBlank" ? t("question.blankHint") : t("richText.syntaxHint")}
          disabled={readOnly}
        />

        {(draft.type === "SingleChoice" || draft.type === "MultipleChoice" || draft.type === "Ordering") && (
          <section className="space-y-2">
            <h3 className="text-sm font-semibold">{draft.type === "Ordering" ? t("question.orderingItems") : t("question.options")}</h3>
            {lineEditor("options")}
          </section>
        )}

        {draft.type === "Matching" && (
          <div className="grid gap-4">
            <section className="space-y-2">
              <h3 className="text-sm font-semibold">{t("question.left")}</h3>
              {lineEditor("options")}
            </section>
            <section className="space-y-2">
              <h3 className="text-sm font-semibold">{t("question.right")}</h3>
              <p className="text-xs text-muted-foreground">{t("question.rightHint")}</p>
              {lineEditor("targets")}
            </section>
          </div>
        )}

        {draft.type === "TrueFalse" && (
          <Field label={t("question.correctAnswer")}>
            <NativeSelect value={draft.choices[0] ?? "true"} onChange={(e) => set({ choices: [e.target.value] })}>
              <option value="true">{t("question.true")}</option>
              <option value="false">{t("question.false")}</option>
            </NativeSelect>
          </Field>
        )}

        {draft.type === "FillBlank" && (
          <section className="space-y-3">
            <h3 className="text-sm font-semibold">{t("question.blanks", { count: blankCount })}</h3>
            {blankCount === 0 && <p className="text-sm text-muted-foreground">{t("question.noBlanks")}</p>}
            {!blanksContiguous && <FormMessage>{t("question.blanksGap")}</FormMessage>}
            {Array.from({ length: blankCount }, (_, i) => (
              <Field key={i} label={t("question.blankN", { n: i + 1 })} hint={t("question.acceptedHint")}>
                <Textarea
                  rows={2}
                  value={draft.blanks[i] ?? ""}
                  onChange={(e) => {
                    const next = [...draft.blanks];
                    while (next.length <= i) next.push("");
                    next[i] = e.target.value;
                    set({ blanks: next });
                  }}
                />
              </Field>
            ))}
          </section>
        )}

        {draft.type === "ShortAnswer" && (
          <Field label={t("question.accepted")} hint={t("question.shortAnswerHint")}>
            <Textarea rows={3} value={draft.accepted} onChange={(e) => set({ accepted: e.target.value })} />
          </Field>
        )}

        {(draft.type === "FillBlank" || draft.type === "ShortAnswer") && (
          <div className="flex flex-wrap gap-x-6 gap-y-2">
            <CheckRow checked={draft.ignoreCase} onChange={(e) => set({ ignoreCase: e.target.checked })}>
              {t("question.ignoreCase")}
            </CheckRow>
            <CheckRow checked={draft.ignoreDiacritics} onChange={(e) => set({ ignoreDiacritics: e.target.checked })}>
              {t("question.ignoreDiacritics")}
            </CheckRow>
          </div>
        )}

        {draft.type === "Essay" && (
          <div className="grid gap-4 sm:grid-cols-3">
            <Field label={t("question.essayMin")}>
              <Input type="number" min={0} inputMode="numeric" value={draft.essayMin} onChange={(e) => set({ essayMin: e.target.value })} />
            </Field>
            <Field label={t("question.essayMax")}>
              <Input type="number" min={1} inputMode="numeric" value={draft.essayMax} onChange={(e) => set({ essayMax: e.target.value })} />
            </Field>
            <Field label={t("question.essayCount")}>
              <NativeSelect value={draft.essayCount} onChange={(e) => set({ essayCount: e.target.value as Draft["essayCount"] })}>
                <option value="Words">{t("question.counts.Words")}</option>
                <option value="Characters">{t("question.counts.Characters")}</option>
              </NativeSelect>
            </Field>
          </div>
        )}

        {(draft.type === "SingleChoice" || draft.type === "MultipleChoice" || draft.type === "Matching") && (
          <CheckRow checked={draft.shuffle} onChange={(e) => set({ shuffle: e.target.checked })}>
            {t("question.shuffle")}
          </CheckRow>
        )}

        <div className="grid gap-4 sm:grid-cols-2">
          <Field label={t("question.points")}>
            <Input type="number" min={0.01} max={1000} step="any" required value={draft.points} onChange={(e) => set({ points: e.target.value })} />
          </Field>
          <Field label={t("question.tags")} hint={t("question.tagsHint")}>
            <Input value={draft.tags} onChange={(e) => set({ tags: e.target.value })} />
          </Field>
        </div>

        <div className="grid gap-4 sm:grid-cols-3">
          <Field label={t("common.framework")}>
            <NativeSelect value={draft.frameworkCode} onChange={(e) => set({ frameworkCode: e.target.value, skillCode: "", levelCode: "" })}>
              <option value="">{t("common.none")}</option>
              {frameworks.map((f) => (
                <option key={f.code} value={f.code}>
                  {localizedName(f.name, locale) || f.code}
                </option>
              ))}
            </NativeSelect>
          </Field>
          <Field label={t("common.skill")}>
            <NativeSelect value={draft.skillCode} onChange={(e) => set({ skillCode: e.target.value })} disabled={!framework}>
              <option value="">{t("common.none")}</option>
              {framework?.skills.map((s) => (
                <option key={s.code} value={s.code}>
                  {localizedName(s.name, locale) || s.code}
                </option>
              ))}
            </NativeSelect>
          </Field>
          <Field label={t("common.level")}>
            <NativeSelect value={draft.levelCode} onChange={(e) => set({ levelCode: e.target.value })} disabled={!framework}>
              <option value="">{t("common.none")}</option>
              {framework?.levels.map((l) => (
                <option key={l.code} value={l.code}>
                  {l.code}
                </option>
              ))}
            </NativeSelect>
          </Field>
        </div>

        <RichTextInput
          label={t("question.explanation")}
          value={draft.explanation}
          onChange={(explanation) => set({ explanation })}
          rows={2}
          hint={t("question.explanationHint")}
          disabled={readOnly}
        />
      </fieldset>

      {error && <FormMessage>{error}</FormMessage>}
      {!readOnly && (
        <div className="flex flex-wrap items-center gap-3">
          <Button type="submit" disabled={pending}>
            {t("common.save")}
          </Button>
          {question && <span className="text-xs text-muted-foreground">{t("question.newVersionHint", { version: question.version + 1 })}</span>}
        </div>
      )}
    </form>
  );
}
