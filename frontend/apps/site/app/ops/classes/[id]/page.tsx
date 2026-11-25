import Link from "next/link";
import { notFound } from "next/navigation";
import { getFormatter, getLocale, getTranslations } from "next-intl/server";
import { ArrowLeft, CalendarPlus, Users } from "lucide-react";
import { requireMe } from "@konnyaku/session";
import { Input } from "@konnyaku/ui/components/ui/input";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@konnyaku/ui/components/ui/table";
import { EmptyState, PageHeader, Panel, StatCard, StatusBadge } from "@konnyaku/ui/kk/blocks";
import { Field, NativeSelect } from "@konnyaku/ui/kk/field";
import { ActionButton, ActionForm, FormSheet } from "../../action-form";
import { ChangeHistory } from "../../history";
import { mergeHistory, type HistoryEntry } from "../../history-entry";
import { emptyPage, getAccess, getJson, localized, namesOf, statusTone, type Branch, type ClassRow, type Page, type Program, type Room, type StaffMember } from "../../lib";

type Session = { id: string; sequenceNo: number; startsAt: string; endsAt: string; teacherId: string | null; roomId: string | null; status: string };
type RosterEntry = { enrollmentId: string; studentId: string; status: string };
type Pattern = { daysOfWeek: number[]; startTime: string; teacherId: string | null; roomId: string | null; effectiveFrom: string };

const isoDays = [1, 2, 3, 4, 5, 6, 7] as const;
const deliveryModes = ["Online", "Offline", "Hybrid"] as const;
const bookingModes = ["Fixed", "Flexible"] as const;

export default async function ClassPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const { token } = await requireMe("staff");
  const t = await getTranslations("admin");
  const format = await getFormatter();
  const locale = await getLocale();
  const [cls, sessions, roster, staffPage, programs, branches, rooms, pattern, access, classHistory, scheduleHistory] = await Promise.all([
    getJson<ClassRow | null>(`/api/academic/classes/${id}`, token, null),
    getJson<Session[]>(`/api/scheduling/classes/${id}/sessions`, token, []),
    getJson<RosterEntry[]>(`/api/academic/classes/${id}/roster`, token, []),
    getJson<Page<StaffMember>>("/api/identity/staff?pageSize=200", token, emptyPage()),
    getJson<Program[]>("/api/academic/programs", token, []),
    getJson<Branch[]>("/api/organization/branches", token, []),
    getJson<Room[]>("/api/organization/rooms", token, []),
    getJson<Pattern | null>(`/api/scheduling/classes/${id}/pattern`, token, null),
    getAccess(token),
    getJson<HistoryEntry[]>(`/api/academic/history/${id}`, token, []),
    getJson<HistoryEntry[]>(`/api/scheduling/history/${id}`, token, []),
  ]);
  if (!cls) notFound();
  const staff = staffPage.items;
  // Names of the students on the roster only (a center has thousands).
  const studentName = await namesOf("/api/crm/students", roster.map((r) => r.studentId), token);
  const teachers = staff.filter((s) => s.roles.includes("Teacher") && s.status === "Active");
  const teacherName = new Map(staff.map((s) => [s.userId, s.displayName]));
  const levels = programs.flatMap((p) => p.levels.map((level) => ({ ...level, program: p })));
  const branchRooms = rooms.filter((r) => r.branchId === cls.branchId && r.isActive);
  const done = sessions.filter((s) => s.status === "Completed").length;
  const scheduled = sessions.some((s) => s.status !== "Cancelled");
  const closed = cls.status === "Completed" || cls.status === "Cancelled";
  const canEdit = access.can("academic.classes", cls.branchId);
  const canSchedule = access.can("scheduling.schedules", cls.branchId) && !closed;
  const names: Record<string, string> = Object.fromEntries([
    ...branches.map((b) => [b.id, localized(b.name, locale)]),
    ...levels.map((l) => [l.id, `${l.program.code} · ${localized(l.name, locale)}`]),
    ...staff.map((s) => [s.userId, s.displayName]),
    ...rooms.map((r) => [r.id, r.code]),
  ]);

  const scheduleFields = (current: Pattern | null) => (
    <>
      <fieldset className="grid gap-2">
        <legend className="mb-2 text-sm font-medium">{t("classes.days")}</legend>
        <div className="flex flex-wrap gap-2">
          {isoDays.map((day) => (
            <label
              key={day}
              className="cursor-pointer rounded-md border px-3 py-1.5 text-sm font-medium transition-colors select-none hover:bg-accent has-[:checked]:border-primary has-[:checked]:bg-primary has-[:checked]:text-primary-foreground"
            >
              <input type="checkbox" name="daysOfWeek[]" value={day} data-kind="number" className="sr-only" defaultChecked={current?.daysOfWeek.includes(day)} />
              {t(`days.${day}`)}
            </label>
          ))}
        </div>
      </fieldset>
      <div className="grid gap-3 sm:grid-cols-2">
        <Field label={t("classes.startTime")}>
          <Input name="startTime" type="time" required defaultValue={current?.startTime.slice(0, 5) ?? "19:00"} />
        </Field>
        <Field label={t("classes.teacher")}>
          <NativeSelect name="teacherId" defaultValue={current?.teacherId ?? ""}>
            <option value="">{t("classes.noTeacher")}</option>
            {teachers.map((s) => <option key={s.userId} value={s.userId}>{s.displayName}</option>)}
          </NativeSelect>
        </Field>
      </div>
      {branchRooms.length > 0 && (
        <Field label={t("classes.room")}>
          <NativeSelect name="roomId" defaultValue={current?.roomId ?? ""}>
            <option value="">{t("classes.noRoom")}</option>
            {branchRooms.map((r) => <option key={r.id} value={r.id}>{t("rooms.option", { code: r.code, capacity: r.capacity })}</option>)}
          </NativeSelect>
        </Field>
      )}
    </>
  );

  return (
    <div className="space-y-6">
      <Link href="/ops/classes" className="inline-flex items-center gap-1.5 text-sm text-muted-foreground hover:text-foreground">
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
          mode: cls.bookingMode === "Flexible" ? `${t(`modes.${cls.deliveryMode}`)} · ${t("bookingModes.Flexible")}` : t(`modes.${cls.deliveryMode}`),
          enrolled: cls.enrolledCount,
          capacity: cls.capacity,
          startsOn: cls.startsOn,
          minutes: cls.sessionMinutes,
        })}
        actions={
          canEdit && (
            <div className="flex flex-wrap items-start gap-2">
              {!closed && (
                <FormSheet triggerLabel={t("edit")} title={t("classes.edit", { code: cls.code })} description={t("classes.editHint")} variant="outline" icon="edit">
                  <ActionForm api={`academic/classes/${cls.id}`} method="PUT" submitLabel={t("save")}>
                    <Field label={t("fields.code")}><Input name="code" required defaultValue={cls.code} /></Field>
                    <Field label={t("classes.level")} hint={scheduled || cls.enrolledCount > 0 ? t("classes.lockedHint") : undefined}>
                      {scheduled || cls.enrolledCount > 0 ? (
                        <>
                          <input type="hidden" name="courseLevelId" value={cls.courseLevelId} />
                          <NativeSelect disabled defaultValue={cls.courseLevelId}>
                            <option value={cls.courseLevelId}>{names[cls.courseLevelId]}</option>
                          </NativeSelect>
                        </>
                      ) : (
                        <NativeSelect name="courseLevelId" required defaultValue={cls.courseLevelId}>
                          {levels.filter((l) => l.isActive || l.id === cls.courseLevelId).map((l) => <option key={l.id} value={l.id}>{names[l.id]}</option>)}
                        </NativeSelect>
                      )}
                    </Field>
                    <Field label={t("classes.branch")} hint={scheduled ? t("classes.lockedHint") : undefined}>
                      {scheduled ? (
                        <>
                          <input type="hidden" name="branchId" value={cls.branchId} />
                          <NativeSelect disabled defaultValue={cls.branchId}><option value={cls.branchId}>{names[cls.branchId]}</option></NativeSelect>
                        </>
                      ) : (
                        <NativeSelect name="branchId" required defaultValue={cls.branchId}>
                          {branches
                            .filter((b) => b.id === cls.branchId || (b.isActive && access.can("academic.classes", b.id)))
                            .map((b) => <option key={b.id} value={b.id}>{localized(b.name, locale)}</option>)}
                        </NativeSelect>
                      )}
                    </Field>
                    <div className="grid grid-cols-2 gap-3">
                      <Field label={t("classes.mode")} hint={t("classes.modeHint")}>
                        <NativeSelect name="deliveryMode" required defaultValue={cls.deliveryMode}>
                          {deliveryModes.map((m) => <option key={m} value={m}>{t(`modes.${m}`)}</option>)}
                        </NativeSelect>
                      </Field>
                      <Field label={t("classes.capacity")} hint={t("classes.capacityHint", { enrolled: cls.enrolledCount })}>
                        <Input name="capacity" type="number" min={Math.max(1, cls.enrolledCount)} required defaultValue={cls.capacity} />
                      </Field>
                    </div>
                    <Field label={t("classes.startsOn")} hint={scheduled ? t("classes.lockedHint") : undefined}>
                      {scheduled ? (
                        <>
                          <input type="hidden" name="startsOn" value={cls.startsOn} />
                          <Input type="date" disabled defaultValue={cls.startsOn} />
                        </>
                      ) : (
                        <Input name="startsOn" type="date" required defaultValue={cls.startsOn} />
                      )}
                    </Field>
                    <Field label={t("classes.bookingMode")} hint={t("classes.bookingModeHint")}>
                      <NativeSelect name="bookingMode" defaultValue={cls.bookingMode ?? "Fixed"}>
                        {bookingModes.map((m) => <option key={m} value={m}>{t(`bookingModes.${m}`)}</option>)}
                      </NativeSelect>
                    </Field>
                  </ActionForm>
                </FormSheet>
              )}
              {closed ? (
                <ActionButton api={`academic/classes/${cls.id}/reopen`} label={t("classes.reopen")} />
              ) : (
                <>
                  <ActionButton
                    api={`academic/classes/${cls.id}/close`}
                    body={{ status: "Completed" }}
                    label={t("classes.complete")}
                    confirm={t("classes.completeConfirm")}
                  />
                  <ActionButton
                    api={`academic/classes/${cls.id}/close`}
                    body={{ status: "Cancelled" }}
                    label={t("classes.cancel")}
                    variant="destructive"
                    confirm={t("classes.cancelConfirm")}
                  />
                </>
              )}
            </div>
          )
        }
      />
      <div className="grid gap-4 sm:grid-cols-3">
        <StatCard label={t("classes.seats")} value={t("classes.seatsValue", { enrolled: cls.enrolledCount, capacity: cls.capacity })} icon={Users} />
        <StatCard label={t("classes.sessionsTotal")} count={sessions.length} icon={CalendarPlus} tone="success" />
        <StatCard label={t("classes.sessionsDone")} count={done} tone="muted" />
      </div>

      <div className="grid gap-6 lg:grid-cols-[1.6fr_1fr]">
        <Panel
          title={t("classes.schedule")}
          bodyClassName={sessions.length === 0 ? "p-5" : "p-0"}
          action={
            scheduled && canSchedule && (
              <FormSheet triggerLabel={t("classes.replan")} title={t("classes.replan")} description={t("classes.replanHint")} variant="outline" size="sm" icon="edit">
                <ActionForm api={`scheduling/classes/${cls.id}/schedule`} method="PUT" submitLabel={t("classes.replanSubmit")}>
                  {scheduleFields(pattern)}
                  <Field label={t("classes.effectiveFrom")} hint={t("classes.effectiveFromHint")}>
                    <Input name="effectiveFrom" type="date" />
                  </Field>
                </ActionForm>
              </FormSheet>
            )
          }
        >
          {sessions.length === 0 ? (
            canSchedule ? (
              <div className="space-y-4">
                <p className="text-sm text-muted-foreground">{t("classes.notScheduled")}</p>
                <ActionForm api={`scheduling/classes/${id}/schedule`} submitLabel={t("classes.scheduleSubmit")}>
                  {scheduleFields(null)}
                </ActionForm>
              </div>
            ) : (
              <EmptyState icon={CalendarPlus} title={t("classes.notScheduledReadOnly")} />
            )
          ) : (
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead className="pl-5 text-right">{t("classes.sequence")}</TableHead>
                  <TableHead>{t("classes.when")}</TableHead>
                  <TableHead>{t("classes.teacher")}</TableHead>
                  <TableHead className="hidden md:table-cell">{t("classes.room")}</TableHead>
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
                    <TableCell className="hidden md:table-cell">{s.roomId ? names[s.roomId] : <span className="text-muted-foreground">{t("classes.noRoom")}</span>}</TableCell>
                    <TableCell className="pr-5"><StatusBadge tone={statusTone[s.status]}>{t(`sessionStatus.${s.status}`)}</StatusBadge></TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
        </Panel>

        <div className="space-y-6">
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
      <Panel title={t("history.title")} description={t("history.classHint")}>
        <ChangeHistory entries={mergeHistory([classHistory, scheduleHistory])} names={names} />
      </Panel>
    </div>
  );
}
