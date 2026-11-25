"use client";

import { useMemo, useState, type FormEvent } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Archive, ArchiveRestore, BookOpenText, Eye, FileQuestion, Pencil, Plus } from "lucide-react";
import { Button } from "@konnyaku/ui/components/animate-ui/components/buttons/button";
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from "@konnyaku/ui/components/animate-ui/components/radix/sheet";
import { Input } from "@konnyaku/ui/components/ui/input";
import { EmptyState, FormMessage, Panel, StatusBadge } from "@konnyaku/ui/kk/blocks";
import { CheckRow, Field, NativeSelect } from "@konnyaku/ui/kk/field";
import { callApi } from "@/assessment/client";
import { fromHtml, plainText, toHtml } from "@/assessment/rich-text";
import { questionTypes, type BankDetail, type Framework, type Group, type Question } from "@/assessment/types";
import { localizedName, useErrorText } from "../../authoring";
import { QuestionForm } from "./question-form";
import { RichTextInput } from "./rich-text-input";

type Editing = { kind: "question"; question: Question | null; groupId: string | null } | { kind: "group"; group: Group | null } | null;

/** Reading groups and questions of one bank: filter, add, edit (new version), archive / restore. */
export function BankEditor({ detail, frameworks }: { detail: BankDetail; frameworks: Framework[] }) {
  const t = useTranslations("assessmentAuthoring");
  const locale = useLocale();
  const errorText = useErrorText();
  const canEdit = detail.bank.canEdit;
  const [groups, setGroups] = useState(detail.groups);
  const [questions, setQuestions] = useState(detail.questions);
  const [editing, setEditing] = useState<Editing>(null);
  const [type, setType] = useState("");
  const [skill, setSkill] = useState("");
  const [tag, setTag] = useState("");
  const [group, setGroup] = useState("");
  const [showArchived, setShowArchived] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const skills = useMemo(() => {
    const map = new Map<string, string>();
    for (const f of frameworks) for (const s of f.skills) if (!map.has(s.code)) map.set(s.code, localizedName(s.name, locale) || s.code);
    return map;
  }, [frameworks, locale]);
  const tags = useMemo(() => [...new Set(questions.flatMap((q) => q.tags))].sort(), [questions]);
  const archivedCount = questions.filter((q) => q.archived).length;
  const groupTitle = (id: string | null) => groups.find((g) => g.id === id)?.title;

  const visible = questions.filter(
    (q) =>
      (showArchived || !q.archived) &&
      (!type || q.type === type) &&
      (!skill || q.skillCode === skill) &&
      (!tag || q.tags.includes(tag)) &&
      (!group || (group === "none" ? !q.groupId : q.groupId === group)),
  );

  function upsertQuestion(question: Question) {
    setQuestions((list) => (list.some((q) => q.id === question.id) ? list.map((q) => (q.id === question.id ? question : q)) : [...list, question]));
  }

  function refreshGroupCounts(list: Question[]) {
    setGroups((gs) => gs.map((g) => ({ ...g, questionCount: list.filter((q) => q.groupId === g.id && !q.archived).length })));
  }

  async function setArchived(question: Question, archived: boolean) {
    setError(null);
    const result = await callApi<Question>(`/teach/api/assessment/questions/${question.id}/${archived ? "archive" : "restore"}`, "POST");
    if (!result.ok) {
      setError(errorText(result.code, result.params));
      return;
    }
    const next = questions.map((q) => (q.id === result.data.id ? result.data : q));
    setQuestions(next);
    refreshGroupCounts(next);
  }

  const sheetTitle =
    editing?.kind === "group"
      ? editing.group
        ? canEdit
          ? t("groups.edit")
          : editing.group.title
        : t("groups.new")
      : editing?.question
        ? canEdit
          ? t("question.edit", { version: editing.question.version })
          : t("question.view")
        : t("question.new");

  return (
    <div className="space-y-6">
      {error && <FormMessage>{error}</FormMessage>}

      <Panel
        title={t("groups.title")}
        description={t("groups.hint")}
        action={
          canEdit && (
            <Button variant="outline" size="sm" onClick={() => setEditing({ kind: "group", group: null })}>
              <Plus />
              {t("groups.new")}
            </Button>
          )
        }
        bodyClassName="p-0"
      >
        {groups.length === 0 ? (
          <p className="px-5 py-4 text-sm text-muted-foreground">{t("groups.empty")}</p>
        ) : (
          <ul className="divide-y">
            {groups.map((g) => (
              <li key={g.id} className="flex flex-col gap-2 px-5 py-3 sm:flex-row sm:items-center sm:justify-between">
                <div className="min-w-0 space-y-0.5">
                  <p className="font-medium">{g.title}</p>
                  <p className="line-clamp-2 text-sm text-muted-foreground">{plainText(g.passage, 200)}</p>
                </div>
                <div className="flex shrink-0 items-center gap-2">
                  <span className="text-xs text-muted-foreground">{t("groups.questionCount", { count: g.questionCount })}</span>
                  <Button variant="ghost" size="sm" onClick={() => setEditing({ kind: "group", group: g })}>
                    {canEdit ? <Pencil /> : <Eye />}
                    {canEdit ? t("common.edit") : t("common.view")}
                  </Button>
                  {canEdit && (
                    <Button variant="ghost" size="sm" onClick={() => setEditing({ kind: "question", question: null, groupId: g.id })}>
                      <Plus />
                      {t("groups.addQuestion")}
                    </Button>
                  )}
                </div>
              </li>
            ))}
          </ul>
        )}
      </Panel>

      <Panel
        title={t("question.listTitle", { count: questions.filter((q) => !q.archived).length })}
        action={
          canEdit && (
            <Button size="sm" onClick={() => setEditing({ kind: "question", question: null, groupId: null })}>
              <Plus />
              {t("question.new")}
            </Button>
          )
        }
        bodyClassName="p-0"
      >
        <div className="grid gap-3 border-b px-5 py-4 sm:grid-cols-2 lg:grid-cols-4">
          <Field label={t("question.type")}>
            <NativeSelect value={type} onChange={(e) => setType(e.target.value)}>
              <option value="">{t("common.all")}</option>
              {questionTypes.map((x) => (
                <option key={x} value={x}>
                  {t(`types.${x}`)}
                </option>
              ))}
            </NativeSelect>
          </Field>
          <Field label={t("common.skill")}>
            <NativeSelect value={skill} onChange={(e) => setSkill(e.target.value)}>
              <option value="">{t("common.all")}</option>
              {[...skills].map(([code, name]) => (
                <option key={code} value={code}>
                  {name}
                </option>
              ))}
            </NativeSelect>
          </Field>
          <Field label={t("question.tag")}>
            <NativeSelect value={tag} onChange={(e) => setTag(e.target.value)}>
              <option value="">{t("common.all")}</option>
              {tags.map((x) => (
                <option key={x} value={x}>
                  {x}
                </option>
              ))}
            </NativeSelect>
          </Field>
          <Field label={t("question.group")}>
            <NativeSelect value={group} onChange={(e) => setGroup(e.target.value)}>
              <option value="">{t("common.all")}</option>
              <option value="none">{t("question.noGroup")}</option>
              {groups.map((g) => (
                <option key={g.id} value={g.id}>
                  {g.title}
                </option>
              ))}
            </NativeSelect>
          </Field>
          <CheckRow checked={showArchived} onChange={(e) => setShowArchived(e.target.checked)} className="sm:col-span-2 lg:col-span-4">
            {t("question.showArchived", { count: archivedCount })}
          </CheckRow>
        </div>

        {visible.length === 0 ? (
          <div className="p-5">
            <EmptyState icon={FileQuestion} title={questions.length === 0 ? t("question.empty") : t("question.noMatch")} />
          </div>
        ) : (
          <ul className="divide-y">
            {visible.map((q) => (
              <li key={q.id} className="flex flex-col gap-3 px-5 py-3 lg:flex-row lg:items-start lg:justify-between">
                <div className="min-w-0 space-y-1.5">
                  <div className="flex flex-wrap items-center gap-1.5">
                    <StatusBadge tone="primary">{t(`types.${q.type}`)}</StatusBadge>
                    {q.archived && <StatusBadge tone="warning">{t("question.archived")}</StatusBadge>}
                    <span className="text-xs text-muted-foreground">{t("question.version", { version: q.version })}</span>
                  </div>
                  <p className="text-sm break-words">{plainText(q.stem) || "—"}</p>
                  <div className="flex flex-wrap items-center gap-x-3 gap-y-1 text-xs text-muted-foreground">
                    <span>{t("question.pointsValue", { points: q.points })}</span>
                    {q.skillCode && <span>{skills.get(q.skillCode) ?? q.skillCode}</span>}
                    {q.levelCode && <span>{q.levelCode}</span>}
                    {q.groupId && (
                      <span className="inline-flex items-center gap-1">
                        <BookOpenText className="size-3" />
                        {groupTitle(q.groupId)}
                      </span>
                    )}
                    {q.tags.map((x) => (
                      <span key={x} className="rounded bg-muted px-1.5 py-0.5">
                        {x}
                      </span>
                    ))}
                  </div>
                </div>
                <div className="flex shrink-0 items-center gap-1">
                  <Button variant="ghost" size="sm" onClick={() => setEditing({ kind: "question", question: q, groupId: q.groupId })}>
                    {canEdit ? <Pencil /> : <Eye />}
                    {canEdit ? t("common.edit") : t("common.view")}
                  </Button>
                  {canEdit && (
                    <Button variant="ghost" size="sm" onClick={() => setArchived(q, !q.archived)}>
                      {q.archived ? <ArchiveRestore /> : <Archive />}
                      {q.archived ? t("question.restore") : t("question.archive")}
                    </Button>
                  )}
                </div>
              </li>
            ))}
          </ul>
        )}
      </Panel>

      <Sheet open={editing !== null} onOpenChange={(open) => !open && setEditing(null)}>
        <SheetContent className="w-full overflow-y-auto sm:w-[40rem] sm:max-w-2xl" closeLabel={t("common.close")}>
          <SheetHeader className="border-b">
            <SheetTitle>{sheetTitle}</SheetTitle>
            {editing?.kind === "question" && <SheetDescription>{t("question.sheetHint")}</SheetDescription>}
          </SheetHeader>
          <div className="px-4 pb-6">
            {editing?.kind === "question" && (
              <QuestionForm
                key={editing.question?.id ?? `new-${editing.groupId ?? ""}`}
                bankId={detail.bank.id}
                question={editing.question}
                groups={groups}
                frameworks={frameworks}
                languageCode={detail.bank.languageCode}
                defaultGroupId={editing.groupId}
                readOnly={!canEdit}
                onSaved={(saved) => {
                  upsertQuestion(saved);
                  refreshGroupCounts(questions.some((q) => q.id === saved.id) ? questions.map((q) => (q.id === saved.id ? saved : q)) : [...questions, saved]);
                  setEditing(null);
                }}
              />
            )}
            {editing?.kind === "group" && (
              <GroupForm
                key={editing.group?.id ?? "new"}
                bankId={detail.bank.id}
                group={editing.group}
                readOnly={!canEdit}
                onSaved={(saved) => {
                  setGroups((gs) => (gs.some((g) => g.id === saved.id) ? gs.map((g) => (g.id === saved.id ? saved : g)) : [...gs, saved]));
                  setEditing(null);
                }}
              />
            )}
          </div>
        </SheetContent>
      </Sheet>
    </div>
  );
}

/** Reading group: a title and a passage shared by its questions. */
function GroupForm({ bankId, group, readOnly, onSaved }: { bankId: string; group: Group | null; readOnly: boolean; onSaved: (group: Group) => void }) {
  const t = useTranslations("assessmentAuthoring");
  const errorText = useErrorText();
  const [title, setTitle] = useState(group?.title ?? "");
  const [passage, setPassage] = useState(() => fromHtml(group?.passage));
  const [pending, setPending] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function onSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setPending(true);
    setError(null);
    const body = { title, passage: toHtml(passage) };
    const result = group
      ? await callApi<Group>(`/teach/api/assessment/groups/${group.id}`, "PUT", body)
      : await callApi<Group>(`/teach/api/assessment/banks/${bankId}/groups`, "POST", body);
    setPending(false);
    if (!result.ok) {
      setError(errorText(result.code, result.params));
      return;
    }
    onSaved(result.data);
  }

  return (
    <form onSubmit={onSubmit} className="space-y-4">
      <fieldset disabled={readOnly || pending} className="space-y-4">
        <Field label={t("groups.groupTitle")}>
          <Input value={title} required maxLength={200} onChange={(e) => setTitle(e.target.value)} />
        </Field>
        <RichTextInput label={t("groups.passage")} value={passage} onChange={setPassage} rows={10} hint={t("richText.syntaxHint")} disabled={readOnly} />
      </fieldset>
      {error && <FormMessage>{error}</FormMessage>}
      {!readOnly && (
        <Button type="submit" disabled={pending}>
          {t("common.save")}
        </Button>
      )}
    </form>
  );
}
