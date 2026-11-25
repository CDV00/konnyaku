"use client";

import { useState } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useLocale, useTranslations } from "next-intl";
import { Archive, ArchiveRestore, ArrowDown, ArrowUp, Plus, Save, Send, Trash2 } from "lucide-react";
import { Button, buttonVariants } from "@konnyaku/ui/components/animate-ui/components/buttons/button";
import { Input } from "@konnyaku/ui/components/ui/input";
import { Textarea } from "@konnyaku/ui/components/ui/textarea";
import { FormMessage, PageHeader, Panel, StatusBadge } from "@konnyaku/ui/kk/blocks";
import { CheckRow, Field, NativeSelect } from "@konnyaku/ui/kk/field";
import { callApi } from "@/assessment/client";
import { plainText } from "@/assessment/rich-text";
import type { Bank, BankDetail, Framework, Group, Paper, PaperDetail, PaperSection, Question, Rubric, SectionItem } from "@/assessment/types";
import { localizedName, newId, splitList, useErrorText, type Language } from "../../authoring";

type Kind = SectionItem["kind"];

function move<T>(list: T[], index: number, delta: number): T[] {
  const target = index + delta;
  if (target < 0 || target >= list.length) return list;
  const next = [...list];
  [next[index], next[target]] = [next[target]!, next[index]!];
  return next;
}

const numberOrNull = (value: string) => (value.trim() === "" ? null : Number(value));

/** Builds a paper: sections of fixed questions, whole reading groups and random draws, plus rubrics for essays. */
export function PaperBuilder({ detail, banks, languages, frameworks }: {
  detail: PaperDetail | null;
  banks: Bank[];
  languages: Language[];
  frameworks: Framework[];
}) {
  const t = useTranslations("assessmentAuthoring");
  const locale = useLocale();
  const router = useRouter();
  const errorText = useErrorText();
  const paper = detail?.paper ?? null;
  const readOnly = paper ? !paper.canEdit : false;

  const [paperId, setPaperId] = useState(paper?.id ?? null);
  const [archived, setArchived] = useState(paper?.archived ?? false);
  const [title, setTitle] = useState(paper?.title ?? "");
  const [languageCode, setLanguageCode] = useState(paper?.languageCode ?? languages[0]?.code ?? "");
  const [frameworkCode, setFrameworkCode] = useState(paper?.frameworkCode ?? "");
  const [description, setDescription] = useState(paper?.description ?? "");
  const [showRuby, setShowRuby] = useState(paper?.showRuby ?? true);
  const [sections, setSections] = useState<PaperSection[]>(
    () => paper?.layout.sections ?? [{ id: newId("s"), title: t("sections.defaultTitle", { n: 1 }), skillCode: null, timeLimitMinutes: null, shuffleQuestions: false, items: [] }],
  );
  const [rubrics, setRubrics] = useState<Rubric[]>(paper?.layout.rubrics ?? []);
  const [bankCache, setBankCache] = useState<Record<string, BankDetail>>({});
  const [pending, setPending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);

  const framework = frameworks.find((f) => f.code === frameworkCode);
  const languageFrameworks = frameworks.filter((f) => !f.languageCode || f.languageCode === languageCode);
  const languageBanks = banks.filter((b) => b.languageCode === languageCode);
  const skillName = (code: string | null | undefined) => {
    if (!code) return null;
    const skill = framework?.skills.find((s) => s.code === code);
    return skill ? localizedName(skill.name, locale) || code : code;
  };

  // Questions and groups known so far: those the paper names (from the API) and those of the banks opened here.
  const questions = new Map<string, Question>();
  const groups = new Map<string, Group>();
  for (const q of detail?.questions ?? []) questions.set(q.id, q);
  for (const g of detail?.groups ?? []) groups.set(g.id, g);
  for (const b of Object.values(bankCache)) {
    for (const q of b.questions) questions.set(q.id, q);
    for (const g of b.groups) groups.set(g.id, g);
  }
  const bankName = (id: string | null | undefined) => banks.find((b) => b.id === id)?.name ?? t("items.unknownBank");

  async function ensureBank(id: string) {
    if (!id || bankCache[id]) return;
    const result = await callApi<BankDetail>(`/teach/api/assessment/banks/${id}`);
    if (result.ok) setBankCache((cache) => ({ ...cache, [id]: result.data }));
    else setError(errorText(result.code, result.params));
  }

  const updateSection = (id: string, patch: Partial<PaperSection>) => setSections((list) => list.map((s) => (s.id === id ? { ...s, ...patch } : s)));
  const updateItem = (sectionId: string, index: number, patch: Partial<SectionItem>) =>
    setSections((list) => list.map((s) => (s.id === sectionId ? { ...s, items: s.items.map((it, i) => (i === index ? { ...it, ...patch } : it)) } : s)));

  function addSection() {
    setSections((list) => [
      ...list,
      {
        id: newId("s", list.map((s) => s.id)),
        title: t("sections.defaultTitle", { n: list.length + 1 }),
        skillCode: null,
        // Keep "all sections timed or none": copy whether the paper is timed.
        timeLimitMinutes: list.length > 0 && list.every((s) => s.timeLimitMinutes) ? 30 : null,
        shuffleQuestions: false,
        items: [],
      },
    ]);
  }

  function addRubric(preset?: "ielts") {
    setRubrics((list) => [
      ...list,
      preset === "ielts"
        ? {
            id: newId("r", list.map((r) => r.id)),
            name: t("rubrics.ieltsName"),
            criteria: (["tr", "cc", "lr", "gra"] as const).map((c) => ({ id: c, name: t(`rubrics.ielts.${c}`), maxBand: 9 })),
          }
        : { id: newId("r", list.map((r) => r.id)), name: "", criteria: [{ id: newId("c"), name: "", maxBand: 10 }] },
    ]);
  }

  const updateRubric = (id: string, patch: Partial<Rubric>) => setRubrics((list) => list.map((r) => (r.id === id ? { ...r, ...patch } : r)));

  function removeRubric(id: string) {
    setRubrics((list) => list.filter((r) => r.id !== id));
    setSections((list) => list.map((s) => ({ ...s, items: s.items.map((it) => (it.rubricId === id ? { ...it, rubricId: null } : it)) })));
  }

  async function save() {
    setPending(true);
    setError(null);
    setSaved(false);
    const body = {
      title,
      languageCode,
      frameworkCode: frameworkCode || null,
      description: description.trim() || null,
      showRuby,
      layout: { sections, rubrics },
    };
    const result = paperId
      ? await callApi<Paper>(`/teach/api/assessment/papers/${paperId}`, "PUT", body)
      : await callApi<Paper>("/teach/api/assessment/papers", "POST", body);
    setPending(false);
    if (!result.ok) {
      setError(errorText(result.code, result.params));
      return;
    }
    setSections(result.data.layout.sections);
    setRubrics(result.data.layout.rubrics);
    setSaved(true);
    if (!paperId) {
      setPaperId(result.data.id);
      router.replace(`/teach/assessments/papers/${result.data.id}`);
    } else {
      router.refresh();
    }
  }

  async function toggleArchived() {
    if (!paperId) return;
    setError(null);
    const result = await callApi<Paper>(`/teach/api/assessment/papers/${paperId}/${archived ? "restore" : "archive"}`, "POST");
    if (!result.ok) {
      setError(errorText(result.code, result.params));
      return;
    }
    setArchived(result.data.archived);
    router.refresh();
  }

  function itemLabel(item: SectionItem): { title: string; detail: string } {
    if (item.kind === "Question") {
      const q = item.questionId ? questions.get(item.questionId) : undefined;
      return q
        ? { title: plainText(q.stem, 100) || "—", detail: `${t(`types.${q.type}`)} · ${t("items.pointsValue", { points: q.points })}` }
        : { title: t("items.unknownQuestion"), detail: "" };
    }
    if (item.kind === "Group") {
      const g = item.groupId ? groups.get(item.groupId) : undefined;
      return g ? { title: g.title, detail: t("items.groupCount", { count: g.questionCount }) } : { title: t("items.unknownGroup"), detail: "" };
    }
    const filters = [
      item.tags.length ? item.tags.join(", ") : null,
      skillName(item.skillCode),
      item.levelCode ?? null,
    ].filter(Boolean);
    return {
      title: t("items.randomTitle", { count: item.count, bank: bankName(item.bankId) }),
      detail: filters.length ? filters.join(" · ") : t("items.noFilter"),
    };
  }

  const totalItems = sections.reduce((sum, s) => sum + s.items.length, 0);
  const timed = sections.length > 0 && sections.every((s) => (s.timeLimitMinutes ?? 0) > 0);
  const totalMinutes = timed ? sections.reduce((sum, s) => sum + (s.timeLimitMinutes ?? 0), 0) : null;

  const actions = (
    <>
      {paperId && !archived && (
        <Link href={`/teach/assessments/assign?paper=${paperId}`} className={buttonVariants({ variant: "outline" })}>
          <Send />
          {t("papers.assign")}
        </Link>
      )}
      {paperId && !readOnly && (
        <Button variant="ghost" onClick={toggleArchived}>
          {archived ? <ArchiveRestore /> : <Archive />}
          {archived ? t("papers.restore") : t("papers.archive")}
        </Button>
      )}
      {!readOnly && (
        <Button onClick={save} disabled={pending}>
          <Save />
          {t("common.save")}
        </Button>
      )}
    </>
  );

  return (
    <div className="space-y-6">
      <PageHeader
        title={paperId ? title || t("papers.untitled") : t("papers.new")}
        description={
          <span className="flex flex-wrap items-center gap-2">
            <span>{t("papers.summary", { items: totalItems, sections: sections.length })}</span>
            <span>{totalMinutes === null ? t("papers.untimed") : t("papers.minutesValue", { minutes: totalMinutes })}</span>
            {archived && <StatusBadge tone="warning">{t("papers.archived")}</StatusBadge>}
            {readOnly && <StatusBadge tone="warning">{t("papers.readOnly")}</StatusBadge>}
          </span>
        }
        actions={actions}
      />
      {error && <FormMessage>{error}</FormMessage>}
      {saved && !error && <FormMessage tone="success">{t("papers.saved")}</FormMessage>}

      <fieldset disabled={readOnly || pending} className="min-w-0 space-y-6">
        <Panel title={t("papers.settings")}>
          <div className="grid gap-4 sm:grid-cols-2">
            <Field label={t("papers.paperTitle")} className="sm:col-span-2">
              <Input value={title} required maxLength={200} onChange={(e) => setTitle(e.target.value)} />
            </Field>
            <Field label={t("common.language")}>
              <NativeSelect
                value={languageCode}
                onChange={(e) => {
                  setLanguageCode(e.target.value);
                  setFrameworkCode("");
                }}
              >
                {languages.map((l) => (
                  <option key={l.code} value={l.code}>
                    {localizedName(l.name, locale) || l.code}
                  </option>
                ))}
              </NativeSelect>
            </Field>
            <Field label={t("common.framework")} hint={t("papers.frameworkHint")}>
              <NativeSelect value={frameworkCode} onChange={(e) => setFrameworkCode(e.target.value)}>
                <option value="">{t("common.none")}</option>
                {languageFrameworks.map((f) => (
                  <option key={f.code} value={f.code}>
                    {localizedName(f.name, locale) || f.code}
                  </option>
                ))}
              </NativeSelect>
            </Field>
            <Field label={t("common.description")} className="sm:col-span-2">
              <Textarea rows={2} maxLength={2000} value={description} onChange={(e) => setDescription(e.target.value)} />
            </Field>
            <div className="space-y-1 sm:col-span-2">
              <CheckRow checked={showRuby} onChange={(e) => setShowRuby(e.target.checked)}>
                {t("papers.showRuby")}
              </CheckRow>
              <p className="pl-6 text-xs text-muted-foreground">{t("papers.showRubyHint")}</p>
            </div>
          </div>
        </Panel>

        <div className="space-y-4">
          <div className="flex flex-wrap items-end justify-between gap-2">
            <div>
              <h2 className="font-semibold tracking-tight">{t("sections.title")}</h2>
              <p className="text-sm text-muted-foreground">{t("sections.timeHint")}</p>
            </div>
            {!readOnly && (
              <Button variant="outline" size="sm" onClick={addSection}>
                <Plus />
                {t("sections.add")}
              </Button>
            )}
          </div>

          {sections.map((section, index) => (
            <Panel
              key={section.id}
              title={t("sections.number", { n: index + 1 })}
              action={
                !readOnly && (
                  <div className="flex items-center gap-1">
                    <Button variant="ghost" size="icon-sm" disabled={index === 0} onClick={() => setSections((l) => move(l, index, -1))} aria-label={t("common.moveUp")}>
                      <ArrowUp />
                    </Button>
                    <Button
                      variant="ghost"
                      size="icon-sm"
                      disabled={index === sections.length - 1}
                      onClick={() => setSections((l) => move(l, index, 1))}
                      aria-label={t("common.moveDown")}
                    >
                      <ArrowDown />
                    </Button>
                    <Button
                      variant="ghost"
                      size="icon-sm"
                      disabled={sections.length <= 1}
                      onClick={() => setSections((l) => l.filter((s) => s.id !== section.id))}
                      aria-label={t("sections.remove")}
                    >
                      <Trash2 />
                    </Button>
                  </div>
                )
              }
              bodyClassName="space-y-4"
            >
              <div className="grid gap-4 sm:grid-cols-[2fr_1fr_1fr]">
                <Field label={t("sections.sectionTitle")}>
                  <Input value={section.title} maxLength={200} onChange={(e) => updateSection(section.id, { title: e.target.value })} />
                </Field>
                <Field label={t("common.skill")}>
                  <NativeSelect value={section.skillCode ?? ""} onChange={(e) => updateSection(section.id, { skillCode: e.target.value || null })} disabled={!framework}>
                    <option value="">{t("common.none")}</option>
                    {framework?.skills.map((s) => (
                      <option key={s.code} value={s.code}>
                        {localizedName(s.name, locale) || s.code}
                      </option>
                    ))}
                  </NativeSelect>
                </Field>
                <Field label={t("sections.timeLimit")}>
                  <Input
                    type="number"
                    min={1}
                    max={600}
                    inputMode="numeric"
                    value={section.timeLimitMinutes ?? ""}
                    onChange={(e) => updateSection(section.id, { timeLimitMinutes: numberOrNull(e.target.value) })}
                  />
                </Field>
              </div>
              <CheckRow checked={section.shuffleQuestions} onChange={(e) => updateSection(section.id, { shuffleQuestions: e.target.checked })}>
                {t("sections.shuffle")}
              </CheckRow>

              {section.items.length === 0 ? (
                <p className="rounded-lg border border-dashed px-4 py-3 text-sm text-muted-foreground">{t("items.empty")}</p>
              ) : (
                <ol className="divide-y rounded-lg border">
                  {section.items.map((item, i) => {
                    const label = itemLabel(item);
                    return (
                      <li key={`${item.kind}-${item.questionId ?? item.groupId ?? item.bankId}-${i}`} className="space-y-2 px-3 py-3">
                        <div className="flex items-start gap-2">
                          <StatusBadge tone={item.kind === "Random" ? "highlight" : item.kind === "Group" ? "success" : "primary"}>{t(`items.kinds.${item.kind}`)}</StatusBadge>
                          <div className="min-w-0 flex-1">
                            <p className="text-sm break-words">{label.title}</p>
                            {label.detail && <p className="text-xs text-muted-foreground">{label.detail}</p>}
                          </div>
                          {!readOnly && (
                            <div className="flex shrink-0 items-center">
                              <Button
                                variant="ghost"
                                size="icon-sm"
                                disabled={i === 0}
                                onClick={() => updateSection(section.id, { items: move(section.items, i, -1) })}
                                aria-label={t("common.moveUp")}
                              >
                                <ArrowUp />
                              </Button>
                              <Button
                                variant="ghost"
                                size="icon-sm"
                                disabled={i === section.items.length - 1}
                                onClick={() => updateSection(section.id, { items: move(section.items, i, 1) })}
                                aria-label={t("common.moveDown")}
                              >
                                <ArrowDown />
                              </Button>
                              <Button
                                variant="ghost"
                                size="icon-sm"
                                onClick={() => updateSection(section.id, { items: section.items.filter((_, j) => j !== i) })}
                                aria-label={t("common.remove")}
                              >
                                <Trash2 />
                              </Button>
                            </div>
                          )}
                        </div>
                        <div className="grid gap-3 sm:grid-cols-2">
                          <Field label={t("items.points")} hint={t("items.pointsHint")}>
                            <Input
                              type="number"
                              min={0.01}
                              max={1000}
                              step="any"
                              value={item.points ?? ""}
                              onChange={(e) => updateItem(section.id, i, { points: numberOrNull(e.target.value) })}
                            />
                          </Field>
                          <Field label={t("items.rubric")}>
                            <NativeSelect value={item.rubricId ?? ""} onChange={(e) => updateItem(section.id, i, { rubricId: e.target.value || null })}>
                              <option value="">{t("common.none")}</option>
                              {rubrics.map((r) => (
                                <option key={r.id} value={r.id}>
                                  {r.name || t("rubrics.untitled")}
                                </option>
                              ))}
                            </NativeSelect>
                          </Field>
                        </div>
                      </li>
                    );
                  })}
                </ol>
              )}

              {!readOnly && (
                <ItemAdder
                  banks={languageBanks}
                  bankCache={bankCache}
                  ensureBank={ensureBank}
                  framework={framework}
                  onAdd={(item) => updateSection(section.id, { items: [...section.items, item] })}
                />
              )}
            </Panel>
          ))}
        </div>

        <Panel
          title={t("rubrics.title")}
          description={t("rubrics.hint")}
          action={
            !readOnly && (
              <div className="flex flex-wrap gap-2">
                <Button variant="outline" size="sm" onClick={() => addRubric("ielts")}>
                  <Plus />
                  {t("rubrics.addIelts")}
                </Button>
                <Button variant="outline" size="sm" onClick={() => addRubric()}>
                  <Plus />
                  {t("rubrics.add")}
                </Button>
              </div>
            )
          }
          bodyClassName="space-y-4"
        >
          {rubrics.length === 0 && <p className="text-sm text-muted-foreground">{t("rubrics.empty")}</p>}
          {rubrics.map((rubric) => (
            <div key={rubric.id} className="space-y-3 rounded-lg border p-3">
              <div className="flex items-end gap-2">
                <Field label={t("rubrics.name")} className="flex-1">
                  <Input value={rubric.name} maxLength={200} onChange={(e) => updateRubric(rubric.id, { name: e.target.value })} />
                </Field>
                {!readOnly && (
                  <Button variant="ghost" size="icon" onClick={() => removeRubric(rubric.id)} aria-label={t("rubrics.remove")}>
                    <Trash2 />
                  </Button>
                )}
              </div>
              <ul className="space-y-2">
                {rubric.criteria.map((criterion) => (
                  <li key={criterion.id} className="flex items-end gap-2">
                    <Field label={t("rubrics.criterion")} className="flex-1">
                      <Input
                        value={criterion.name}
                        maxLength={200}
                        onChange={(e) =>
                          updateRubric(rubric.id, { criteria: rubric.criteria.map((c) => (c.id === criterion.id ? { ...c, name: e.target.value } : c)) })
                        }
                      />
                    </Field>
                    <Field label={t("rubrics.maxBand")} className="w-24">
                      <Input
                        type="number"
                        min={1}
                        max={100}
                        inputMode="numeric"
                        value={criterion.maxBand}
                        onChange={(e) =>
                          updateRubric(rubric.id, {
                            criteria: rubric.criteria.map((c) => (c.id === criterion.id ? { ...c, maxBand: Number(e.target.value) } : c)),
                          })
                        }
                      />
                    </Field>
                    {!readOnly && (
                      <Button
                        variant="ghost"
                        size="icon"
                        disabled={rubric.criteria.length <= 1}
                        onClick={() => updateRubric(rubric.id, { criteria: rubric.criteria.filter((c) => c.id !== criterion.id) })}
                        aria-label={t("common.remove")}
                      >
                        <Trash2 />
                      </Button>
                    )}
                  </li>
                ))}
              </ul>
              {!readOnly && rubric.criteria.length < 10 && (
                <Button
                  variant="outline"
                  size="sm"
                  onClick={() =>
                    updateRubric(rubric.id, {
                      criteria: [...rubric.criteria, { id: newId("c", rubric.criteria.map((c) => c.id)), name: "", maxBand: rubric.criteria.at(-1)?.maxBand ?? 10 }],
                    })
                  }
                >
                  <Plus />
                  {t("rubrics.addCriterion")}
                </Button>
              )}
            </div>
          ))}
        </Panel>
      </fieldset>

      {!readOnly && (
        <div className="flex flex-wrap gap-2">
          <Button onClick={save} disabled={pending}>
            <Save />
            {t("common.save")}
          </Button>
        </div>
      )}
    </div>
  );
}

/** Picks a fixed question, a whole reading group or a random draw from a bank the teacher can read. */
function ItemAdder({ banks, bankCache, ensureBank, framework, onAdd }: {
  banks: Bank[];
  bankCache: Record<string, BankDetail>;
  ensureBank: (id: string) => Promise<void>;
  framework: Framework | undefined;
  onAdd: (item: SectionItem) => void;
}) {
  const t = useTranslations("assessmentAuthoring");
  const locale = useLocale();
  const [kind, setKind] = useState<Kind>("Question");
  const [bankId, setBankId] = useState("");
  const [questionId, setQuestionId] = useState("");
  const [groupId, setGroupId] = useState("");
  const [tags, setTags] = useState("");
  const [skillCode, setSkillCode] = useState("");
  const [levelCode, setLevelCode] = useState("");
  const [count, setCount] = useState("5");
  const [loading, setLoading] = useState(false);

  const bank = bankId ? bankCache[bankId] : undefined;
  const available = (bank?.questions ?? []).filter((q) => !q.archived);

  async function pickBank(id: string) {
    setBankId(id);
    setQuestionId("");
    setGroupId("");
    if (id && !bankCache[id]) {
      setLoading(true);
      await ensureBank(id);
      setLoading(false);
    }
  }

  const canAdd =
    !!bankId && (kind === "Question" ? !!questionId : kind === "Group" ? !!groupId : Number(count) >= 1 && Number(count) <= 100);

  function add() {
    if (!canAdd) return;
    const base: SectionItem = { kind, tags: [], count: 1, points: null, rubricId: null, bankId };
    if (kind === "Question") onAdd({ ...base, questionId });
    else if (kind === "Group") onAdd({ ...base, groupId });
    else onAdd({ ...base, tags: splitList(tags), skillCode: skillCode || null, levelCode: levelCode || null, count: Number(count) });
    setQuestionId("");
    setGroupId("");
  }

  if (banks.length === 0) return <p className="text-sm text-muted-foreground">{t("items.noBanks")}</p>;

  return (
    <div className="space-y-3 rounded-lg bg-muted/40 p-3">
      <p className="text-sm font-medium">{t("items.add")}</p>
      <div className="grid gap-3 sm:grid-cols-2">
        <Field label={t("items.kind")}>
          <NativeSelect value={kind} onChange={(e) => setKind(e.target.value as Kind)}>
            {(["Question", "Group", "Random"] as const).map((k) => (
              <option key={k} value={k}>
                {t(`items.kinds.${k}`)}
              </option>
            ))}
          </NativeSelect>
        </Field>
        <Field label={t("items.bank")}>
          <NativeSelect value={bankId} onChange={(e) => pickBank(e.target.value)}>
            <option value="">{t("common.choose")}</option>
            {banks.map((b) => (
              <option key={b.id} value={b.id}>
                {t("items.bankOption", { name: b.name, count: b.questionCount })}
              </option>
            ))}
          </NativeSelect>
        </Field>
      </div>
      {loading && <p className="text-xs text-muted-foreground">{t("common.loading")}</p>}

      {kind === "Question" && bank && (
        <Field label={t("items.question")}>
          <NativeSelect value={questionId} onChange={(e) => setQuestionId(e.target.value)}>
            <option value="">{t("common.choose")}</option>
            {available.map((q) => (
              <option key={q.id} value={q.id}>
                {t("items.questionOption", { type: t(`types.${q.type}`), stem: plainText(q.stem, 80) })}
              </option>
            ))}
          </NativeSelect>
        </Field>
      )}

      {kind === "Group" && bank && (
        <Field label={t("items.group")}>
          <NativeSelect value={groupId} onChange={(e) => setGroupId(e.target.value)}>
            <option value="">{t("common.choose")}</option>
            {bank.groups.map((g) => (
              <option key={g.id} value={g.id}>
                {t("items.groupOption", { title: g.title, count: g.questionCount })}
              </option>
            ))}
          </NativeSelect>
        </Field>
      )}

      {kind === "Random" && (
        <div className="grid gap-3 sm:grid-cols-2">
          <Field label={t("items.tags")} hint={t("items.tagsHint")}>
            <Input value={tags} onChange={(e) => setTags(e.target.value)} />
          </Field>
          <Field label={t("items.count")}>
            <Input type="number" min={1} max={100} inputMode="numeric" value={count} onChange={(e) => setCount(e.target.value)} />
          </Field>
          <Field label={t("common.skill")}>
            <NativeSelect value={skillCode} onChange={(e) => setSkillCode(e.target.value)} disabled={!framework}>
              <option value="">{t("common.any")}</option>
              {framework?.skills.map((s) => (
                <option key={s.code} value={s.code}>
                  {localizedName(s.name, locale) || s.code}
                </option>
              ))}
            </NativeSelect>
          </Field>
          <Field label={t("common.level")}>
            <NativeSelect value={levelCode} onChange={(e) => setLevelCode(e.target.value)} disabled={!framework}>
              <option value="">{t("common.any")}</option>
              {framework?.levels.map((l) => (
                <option key={l.code} value={l.code}>
                  {l.code}
                </option>
              ))}
            </NativeSelect>
          </Field>
          <p className="text-xs text-muted-foreground sm:col-span-2">{t("items.randomHint")}</p>
        </div>
      )}

      <Button type="button" size="sm" variant="secondary" onClick={add} disabled={!canAdd}>
        <Plus />
        {t("items.addButton")}
      </Button>
    </div>
  );
}
