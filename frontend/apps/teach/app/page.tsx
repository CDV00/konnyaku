import { getFormatter, getTranslations } from "next-intl/server";
import { apiFetch, currentMonth, requireMe, tenantInfo } from "@konnyaku/session";
import { SessionList, type SessionView } from "@konnyaku/session/schedule";

export default async function HomePage() {
  const { me, token } = await requireMe("staff");
  const response = await apiFetch("/api/scheduling/my/teaching", { token });
  const sessions = response.ok ? ((await response.json()) as SessionView[]) : [];
  const schedule = await getTranslations("schedule");
  const month = currentMonth((await tenantInfo())?.timeZone ?? "UTC");
  const timesheetResponse = await apiFetch(`/api/hr/my/timesheets?from=${month.from}&to=${month.to}`, { token });
  const timesheets = timesheetResponse.ok
    ? ((await timesheetResponse.json()) as { sessionId: string; localDate: string; paidMinutes: number; lateMinutes: number; amount: number; currency: string | null; status: string }[])
    : [];
  const ts = await getTranslations("timesheet");
  const format = await getFormatter();
  const currency = timesheets.find((x) => x.currency)?.currency ?? "VND";
  const total = timesheets.reduce((sum, x) => sum + x.amount, 0);
  const t = await getTranslations("teach");
  const session = await getTranslations("session");
  return (
    <main style={{ fontFamily: "system-ui", padding: 24 }}>
      <h1>{t("title")}</h1>
      <p>{session("greeting", { name: me.displayName })}</p>
      <p>{session("roles", { roles: me.roles.join(", ") })}</p>
      <section>
        <h2>{ts("title")}</h2>
        {timesheets.length === 0 ? (
          <p>{ts("empty")}</p>
        ) : (
          <>
            <p>
              {ts("summary", {
                sessions: timesheets.length,
                minutes: timesheets.reduce((sum, x) => sum + x.paidMinutes, 0),
                amount: format.number(total, { style: "currency", currency }),
              })}
            </p>
            <ul>
              {timesheets.map((x) => (
                <li key={x.sessionId}>
                  {ts("row", {
                    date: format.dateTime(new Date(`${x.localDate}T00:00:00Z`), { dateStyle: "short", timeZone: "UTC" }),
                    minutes: x.paidMinutes,
                    late: x.lateMinutes > 0 ? ts("late", { minutes: x.lateMinutes }) : "",
                  })}
                  {" · "}
                  {x.currency ? format.number(x.amount, { style: "currency", currency: x.currency }) : ts("noRate")}
                  {" · "}
                  {ts(`status.${x.status}`)}
                </li>
              ))}
            </ul>
          </>
        )}
      </section>
      <SessionList canJoin canTakeAttendance sessions={sessions} title={schedule("teaching")} />
      <form method="post" action="/api/session/staff/logout">
        <button type="submit">{session("logout")}</button>
      </form>
    </main>
  );
}
