import Link from "next/link";
import { notFound } from "next/navigation";
import { getFormatter, getTranslations } from "next-intl/server";
import { ArrowLeft, CalendarPlus, Users } from "lucide-react";
import { requireMe } from "@konnyaku/session";
import { Input } from "@konnyaku/ui/components/ui/input";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@konnyaku/ui/components/ui/table";
import { EmptyState, PageHeader, Panel, StatCard, StatusBadge } from "@konnyaku/ui/kk/blocks";
import { Field, NativeSelect } from "@konnyaku/ui/kk/field";
import { ActionForm } from "../../action-form";
import { getJson, statusTone, type ClassRow, type StaffMember, type Student } from "../../lib";

type Session = { id: string; sequenceNo: number; startsAt: string; endsAt: string; teacherId: string | null; status: string };
type RosterEntry = { enrollmentId: string; studentId: string; status: string };

const isoDays = [1, 2, 3, 4, 5, 6, 7] as const;

export default async function ClassPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const { token } = await requireMe("staff");
  const t = await getTranslations("admin");
  const format = await getFormatter();
  const [classes, sessions, roster, students, staff] = await Promise.all([
    getJson<ClassRow[]>("/api/academic/classes", token, []),
    getJson<Session[]>(`/api/scheduling/classes/${id}/sessions`, token, []),
    getJson<RosterEntry[]>(`/api/academic/classes/${id}/roster`, token, []),
    getJson<Student[]>("/api/crm/students", token, []),
    getJson<StaffMember[]>("/api/identity/staff", token, []),
  ]);
  const cls = classes.find((c) => c.id === id);
  if (!cls) notFound();
  const studentName = new Map(students.map((s) => [s.id, s.fullName]));
  const teachers = staff.filter((s) => s.roles.includes("Teacher"));
  const teacherName = new Map(staff.map((s) => [s.userId, s.displayName]));
  const done = sessions.filter((s) => s.status === "Completed").length;

  return (
    <div className="space-y-6">
      <Link href="/classes" className="inline-flex items-center gap-1.5 text-sm text-muted-foreground hover:text-foreground">
        <ArrowLeft className="size-4" />
        {t("classes.back")}
      </Link>
      <PageHeader
        title={
          <span className="flex flex-wrap items-center gap-3">
            {t("classes.detail", { code: cls.code })}
            <StatusBadge tone={statusTone[cls.status]}>{t(`classStatus.${cls.status}`)}</StatusBadge>
          </span>
        }
        description={t("classes.summary", {
          mode: t(`modes.${cls.deliveryMode}`),
          enrolled: cls.enrolledCount,
          capacity: cls.capacity,
          startsOn: cls.startsOn,
          minutes: cls.sessionMinutes,
        })}
      />
      <div className="grid gap-4 sm:grid-cols-3">
        <StatCard label={t("classes.seats")} value={t("classes.seatsValue", { enrolled: cls.enrolledCount, capacity: cls.capacity })} icon={Users} />
        <StatCard label={t("classes.sessionsTotal")} count={sessions.length} icon={CalendarPlus} tone="success" />
        <StatCard label={t("classes.sessionsDone")} count={done} tone="muted" />
      </div>

      <div className="grid gap-6 lg:grid-cols-[1.6fr_1fr]">
        <Panel title={t("classes.schedule")} bodyClassName={sessions.length === 0 ? "p-5" : "p-0"}>
          {sessions.length === 0 ? (
            <div className="space-y-4">
              <p className="text-sm text-muted-foreground">{t("classes.notScheduled")}</p>
              <ActionForm api={`scheduling/classes/${id}/schedule`} submitLabel={t("classes.scheduleSubmit")}>
                <fieldset className="grid gap-2">
                  <legend className="mb-2 text-sm font-medium">{t("classes.days")}</legend>
                  <div className="flex flex-wrap gap-2">
                    {isoDays.map((day) => (
                      <label
                        key={day}
                        className="cursor-pointer rounded-md border px-3 py-1.5 text-sm font-medium transition-colors select-none hover:bg-accent has-[:checked]:border-primary has-[:checked]:bg-primary has-[:checked]:text-primary-foreground"
                      >
                        <input type="checkbox" name="daysOfWeek[]" value={day} data-kind="number" className="sr-only" />
                        {t(`days.${day}`)}
                      </label>
                    ))}
                  </div>
                </fieldset>
                <div className="grid gap-3 sm:grid-cols-2">
                  <Field label={t("classes.startTime")}><Input name="startTime" type="time" required defaultValue="19:00" /></Field>
                  <Field label={t("classes.teacher")}>
                    <NativeSelect name="teacherId">
                      <option value="">{t("classes.noTeacher")}</option>
                      {teachers.map((s) => <option key={s.userId} value={s.userId}>{s.displayName}</option>)}
                    </NativeSelect>
                  </Field>
                </div>
              </ActionForm>
            </div>
          ) : (
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead className="pl-5 text-right">{t("classes.sequence")}</TableHead>
                  <TableHead>{t("classes.when")}</TableHead>
                  <TableHead>{t("classes.teacher")}</TableHead>
                  <TableHead className="pr-5">{t("fields.status")}</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {sessions.map((s) => (
                  <TableRow key={s.id}>
                    <TableCell className="pl-5 text-right tabular-nums text-muted-foreground">{s.sequenceNo}</TableCell>
                    <TableCell className="tabular-nums">
                      {format.dateTime(new Date(s.startsAt), { weekday: "short", day: "2-digit", month: "2-digit", year: "numeric", hour: "2-digit", minute: "2-digit" })}
                    </TableCell>
                    <TableCell>
                      {s.teacherId ? (teacherName.get(s.teacherId) ?? s.teacherId) : <span className="text-muted-foreground">{t("classes.noTeacher")}</span>}
                    </TableCell>
                    <TableCell className="pr-5"><StatusBadge tone={statusTone[s.status]}>{t(`sessionStatus.${s.status}`)}</StatusBadge></TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
        </Panel>

        <Panel title={t("classes.roster")} description={t("classes.enrollHint")} bodyClassName="p-0">
          {roster.length === 0 ? (
            <div className="p-5"><EmptyState icon={Users} title={t("classes.emptyRoster")} /></div>
          ) : (
            <ul className="divide-y">
              {roster.map((r) => (
                <li key={r.enrollmentId} className="flex items-center justify-between gap-3 px-5 py-3">
                  <span className="font-medium">{studentName.get(r.studentId) ?? r.studentId}</span>
                  <StatusBadge tone={statusTone[r.status]}>{t(`enrollmentStatus.${r.status}`)}</StatusBadge>
                </li>
              ))}
            </ul>
          )}
        </Panel>
      </div>
    </div>
  );
}
