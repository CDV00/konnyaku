"use client";

import { useState, type FormEvent } from "react";
import { usePathname, useRouter } from "next/navigation";
import { useFormatter, useTranslations } from "next-intl";
import { Send } from "lucide-react";
import { Button } from "@konnyaku/ui/components/animate-ui/components/buttons/button";
import { Input } from "@konnyaku/ui/components/ui/input";
import { FormMessage, Panel } from "@konnyaku/ui/kk/blocks";
import { CheckRow, Field, NativeSelect } from "@konnyaku/ui/kk/field";
import { callApi } from "@/assessment/client";
import {
  answerVisibilities,
  assignmentKinds,
  errorKey,
  graderModes,
  latePolicies,
  scoreReleases,
  scoringRules,
  type Assignment,
  type Paper,
} from "@/assessment/types";

export type ClassOption = { id: string; code: string; status: string };
export type SessionOption = { id: string; sequenceNo: number; startsAt: string; status: string };
export type StudentOption = { studentId: string; name: string };

/** `datetime-local` value (browser time) of an instant. */
function local(date: Date): string {
  const shifted = new Date(date.getTime() - date.getTimezoneOffset() * 60_000);
  return shifted.toISOString().slice(0, 16);
}

function iso(value: string): string | null {
  return value ? new Date(value).toISOString() : null;
}

export function AssignForm({ papers, classes, classId, paperId, sessions, students }: {
  papers: Paper[];
  classes: ClassOption[];
  classId: string | null;
  paperId: string | null;
  sessions: SessionOption[];
  students: StudentOption[];
}) {
  const t = useTranslations("assessment");
  const format = useFormatter();
  const router = useRouter();
  const pathname = usePathname();
  const [paper, setPaper] = useState(paperId && papers.some((p) => p.id === paperId) ? paperId : (papers[0]?.id ?? ""));
  const [target, setTarget] = useState<"Class" | "Session" | "Students">("Class");
  const [late, setLate] = useState<string>("Accept");
  const [release, setRelease] = useState<string>("OnSubmit");
  const [chosen, setChosen] = useState<string[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState(false);
  const [defaults] = useState(() => {
    const due = new Date(Date.now() + 7 * 86_400_000);
    due.setHours(23, 59, 0, 0);
    return { opens: local(new Date()), due: local(due) };
  });
  const selectedPaper = papers.find((p) => p.id === paper);

  function pickClass(id: string) {
    const params = new URLSearchParams();
    if (paper) params.set("paper", paper);
    if (id) params.set("class", id);
    router.replace(`${pathname}?${params.toString()}`);
  }

  async function onSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = new FormData(event.currentTarget);
    const text = (name: string) => String(form.get(name) ?? "");
    const number = (name: string) => (text(name) ? Number(text(name)) : null);
    setPending(true);
    setError(null);
    const result = await callApi<Assignment>("/teach/api/assessment/assignments", "POST", {
      paperId: paper,
      title: text("title") || selectedPaper?.title || "",
      target,
      classId,
      sessionId: target === "Session" ? text("sessionId") || null : null,
      studentIds: target === "Students" ? chosen : [],
      kind: text("kind"),
      opensAt: iso(text("opensAt")),
      dueAt: iso(text("dueAt")),
      latePolicy: late,
      latePenaltyPercent: late === "Penalty" ? (number("latePenaltyPercent") ?? 0) : 0,
      maxAttempts: number("maxAttempts"),
      scoringRule: text("scoringRule"),
      grader: text("grader"),
      scoreRelease: release,
      releaseAt: release === "AtTime" ? iso(text("releaseAt")) : null,
      answerVisibility: text("answerVisibility"),
      timeLimitMinutes: selectedPaper?.totalMinutes ? null : number("timeLimitMinutes"),
    });
    setPending(false);
    if (result.ok) {
      router.push(`/teach/assessments/assignments/${result.data.id}`);
      return;
    }
    const key = `errors.${errorKey(result.code)}`;
    setError(t.has(key) ? t(key as "errors.unknown") : t("errors.unknown"));
  }

  if (papers.length === 0) {
    return <FormMessage tone="error">{t("assign.noPapers")}</FormMessage>;
  }

  return (
    <form onSubmit={onSubmit} className="grid gap-6 lg:grid-cols-2">
      <Panel title={t("assign.what")}>
        <div className="space-y-4">
          <Field label={t("assign.paper")}>
            <NativeSelect value={paper} onChange={(e) => setPaper(e.target.value)} required>
              {papers.map((p) => (
                <option key={p.id} value={p.id}>{t("assign.paperOption", { title: p.title, count: p.questionCount })}</option>
              ))}
            </NativeSelect>
          </Field>
          <Field label={t("assign.name")} hint={t("assign.nameHint")}>
            <Input name="title" placeholder={selectedPaper?.title} maxLength={200} />
          </Field>
          <Field label={t("assign.kind")}>
            <NativeSelect name="kind" defaultValue="Homework">
              {assignmentKinds.map((k) => <option key={k} value={k}>{t(`kinds.${k}`)}</option>)}
            </NativeSelect>
          </Field>
          <Field label={t("assign.class")}>
            <NativeSelect value={classId ?? ""} onChange={(e) => pickClass(e.target.value)} required>
              <option value="">{t("assign.pickClass")}</option>
              {classes.map((c) => <option key={c.id} value={c.id}>{c.code}</option>)}
            </NativeSelect>
          </Field>
          {classId && (
            <Field label={t("assign.target")}>
              <NativeSelect value={target} onChange={(e) => setTarget(e.target.value as typeof target)}>
                {(["Class", "Session", "Students"] as const).map((x) => <option key={x} value={x}>{t(`targets.${x}`)}</option>)}
              </NativeSelect>
            </Field>
          )}
          {classId && target === "Session" && (
            <Field label={t("assign.session")}>
              <NativeSelect name="sessionId" required>
                {sessions.map((s) => (
                  <option key={s.id} value={s.id}>
                    {t("assign.sessionOption", {
                      number: s.sequenceNo,
                      when: format.dateTime(new Date(s.startsAt), { weekday: "short", day: "2-digit", month: "2-digit", hour: "2-digit", minute: "2-digit" }),
                    })}
                  </option>
                ))}
              </NativeSelect>
            </Field>
          )}
          {classId && target === "Students" && (
            <fieldset className="space-y-2">
              <legend className="text-sm font-medium">{t("assign.students")}</legend>
              {students.length === 0 && <p className="text-sm text-muted-foreground">{t("assign.noStudents")}</p>}
              <div className="grid gap-1 sm:grid-cols-2">
                {students.map((s) => (
                  <CheckRow
                    key={s.studentId}
                    checked={chosen.includes(s.studentId)}
                    onChange={(e) => setChosen((all) => (e.target.checked ? [...all, s.studentId] : all.filter((x) => x !== s.studentId)))}
                  >
                    {s.name}
                  </CheckRow>
                ))}
              </div>
            </fieldset>
          )}
        </div>
      </Panel>

      <Panel title={t("assign.settings")}>
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label={t("assign.opensAt")}>
            <Input type="datetime-local" name="opensAt" defaultValue={defaults.opens} required />
          </Field>
          <Field label={t("assign.dueAt")}>
            <Input type="datetime-local" name="dueAt" defaultValue={defaults.due} />
          </Field>
          <Field label={t("assign.late")}>
            <NativeSelect value={late} onChange={(e) => setLate(e.target.value)}>
              {latePolicies.map((x) => <option key={x} value={x}>{t(`latePolicies.${x}`)}</option>)}
            </NativeSelect>
          </Field>
          {late === "Penalty" ? (
            <Field label={t("assign.penalty")}>
              <Input type="number" name="latePenaltyPercent" min={1} max={100} defaultValue={10} required />
            </Field>
          ) : (
            <div className="hidden sm:block" />
          )}
          <Field label={t("assign.maxAttempts")} hint={t("assign.maxAttemptsHint")}>
            <Input type="number" name="maxAttempts" min={1} max={100} defaultValue={1} />
          </Field>
          <Field label={t("assign.scoring")}>
            <NativeSelect name="scoringRule" defaultValue="Best">
              {scoringRules.map((x) => <option key={x} value={x}>{t(`scoringRules.${x}`)}</option>)}
            </NativeSelect>
          </Field>
          <Field
            label={t("assign.timeLimit")}
            hint={selectedPaper?.totalMinutes ? t("assign.timeFromPaper", { minutes: selectedPaper.totalMinutes }) : t("assign.timeLimitHint")}
          >
            <Input type="number" name="timeLimitMinutes" min={1} max={600} disabled={!!selectedPaper?.totalMinutes} />
          </Field>
          <Field label={t("assign.grader")}>
            <NativeSelect name="grader" defaultValue="Auto">
              {graderModes.map((x) => <option key={x} value={x}>{t(`graders.${x}`)}</option>)}
            </NativeSelect>
          </Field>
          <Field label={t("assign.release")}>
            <NativeSelect value={release} onChange={(e) => setRelease(e.target.value)}>
              {scoreReleases.map((x) => <option key={x} value={x}>{t(`releases.${x}`)}</option>)}
            </NativeSelect>
          </Field>
          {release === "AtTime" ? (
            <Field label={t("assign.releaseAt")}>
              <Input type="datetime-local" name="releaseAt" defaultValue={defaults.due} required />
            </Field>
          ) : (
            <div className="hidden sm:block" />
          )}
          <Field label={t("assign.answers")}>
            <NativeSelect name="answerVisibility" defaultValue="AfterSubmit">
              {answerVisibilities.map((x) => <option key={x} value={x}>{t(`answers.${x}`)}</option>)}
            </NativeSelect>
          </Field>
        </div>
      </Panel>

      <div className="space-y-3 lg:col-span-2">
        {error && <FormMessage>{error}</FormMessage>}
        <Button type="submit" size="lg" disabled={pending || !classId || !paper || (target === "Students" && chosen.length === 0)}>
          <Send />
          {t("assign.submit")}
        </Button>
      </div>
    </form>
  );
}
