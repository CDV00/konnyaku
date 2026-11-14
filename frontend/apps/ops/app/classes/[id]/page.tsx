import Link from "next/link";
import { notFound } from "next/navigation";
import { getFormatter, getTranslations } from "next-intl/server";
import { requireMe } from "@konnyaku/session";
import { ActionForm } from "../../action-form";
import { getJson, type ClassRow, type StaffMember, type Student } from "../../lib";

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

  return (
    <main>
      <p><Link href="/classes">{t("classes.back")}</Link></p>
      <h1>{t("classes.detail", { code: cls.code })}</h1>
      <p>{t("classes.summary", { mode: t(`modes.${cls.deliveryMode}`), enrolled: cls.enrolledCount, capacity: cls.capacity, startsOn: cls.startsOn, minutes: cls.sessionMinutes })}</p>

      <h2>{t("classes.schedule")}</h2>
      {sessions.length === 0 ? (
        <>
          <p className="muted">{t("classes.notScheduled")}</p>
          <ActionForm api={`scheduling/classes/${id}/schedule`} submitLabel={t("classes.scheduleSubmit")}>
            <fieldset>
              <legend>{t("classes.days")}</legend>
              {isoDays.map((day) => (
                <label key={day} className="inline">
                  <input type="checkbox" name="daysOfWeek[]" value={day} data-kind="number" />
                  {t(`days.${day}`)}
                </label>
              ))}
            </fieldset>
            <label>{t("classes.startTime")}<input name="startTime" type="time" required defaultValue="19:00" /></label>
            <label>
              {t("classes.teacher")}
              <select name="teacherId">
                <option value="">{t("classes.noTeacher")}</option>
                {teachers.map((s) => <option key={s.userId} value={s.userId}>{s.displayName}</option>)}
              </select>
            </label>
          </ActionForm>
        </>
      ) : (
        <table>
          <thead>
            <tr>
              <th className="num">{t("classes.sequence")}</th>
              <th>{t("classes.when")}</th>
              <th>{t("classes.teacher")}</th>
              <th>{t("fields.status")}</th>
            </tr>
          </thead>
          <tbody>
            {sessions.map((s) => (
              <tr key={s.id}>
                <td className="num">{s.sequenceNo}</td>
                <td>
                  {format.dateTime(new Date(s.startsAt), { weekday: "short", day: "2-digit", month: "2-digit", year: "numeric", hour: "2-digit", minute: "2-digit" })}
                </td>
                <td>{s.teacherId ? (teacherName.get(s.teacherId) ?? s.teacherId) : <span className="muted">{t("classes.noTeacher")}</span>}</td>
                <td>{t(`sessionStatus.${s.status}`)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      <h2>{t("classes.roster")}</h2>
      {roster.length === 0 ? (
        <p className="muted">{t("classes.emptyRoster")}</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>{t("students.fullName")}</th>
              <th>{t("fields.status")}</th>
            </tr>
          </thead>
          <tbody>
            {roster.map((r) => (
              <tr key={r.enrollmentId}>
                <td>{studentName.get(r.studentId) ?? r.studentId}</td>
                <td>{t(`enrollmentStatus.${r.status}`)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      <p className="muted">{t("classes.enrollHint")}</p>
    </main>
  );
}
