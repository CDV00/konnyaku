import { getFormatter, getTranslations } from "next-intl/server";
import { Banknote, CalendarCheck2, Clock, Receipt } from "lucide-react";
import { apiFetch, currentMonth, requireMe, tenantInfo } from "@konnyaku/session";
import { SessionList, type SessionView } from "@konnyaku/session/schedule";
import { EmptyState, PageHeader, Panel, StatCard, StatusBadge, type Tone } from "@konnyaku/ui/kk/blocks";
import { Fade } from "@konnyaku/ui/components/animate-ui/primitives/effects/fade";
import { TeachOnboarding } from "./teach-onboarding";

type Timesheet = {
  sessionId: string;
  localDate: string;
  paidMinutes: number;
  lateMinutes: number;
  amount: number;
  currency: string | null;
  status: string;
};

const statusTone: Record<string, Tone> = { Draft: "muted", Approved: "primary", Locked: "success" };

export default async function HomePage() {
  const { me, token } = await requireMe("staff");
  const month = currentMonth((await tenantInfo())?.timeZone ?? "UTC");
  const [sessionsResponse, timesheetResponse] = await Promise.all([
    apiFetch("/api/scheduling/my/teaching", { token }),
    apiFetch(`/api/hr/my/timesheets?from=${month.from}&to=${month.to}`, { token }),
  ]);
  const sessions = sessionsResponse.ok ? ((await sessionsResponse.json()) as SessionView[]) : [];
  const timesheets = timesheetResponse.ok ? ((await timesheetResponse.json()) as Timesheet[]) : [];

  const t = await getTranslations("teach");
  const schedule = await getTranslations("schedule");
  const ts = await getTranslations("timesheet");
  const session = await getTranslations("session");
  const format = await getFormatter();
  const currency = timesheets.find((x) => x.currency)?.currency ?? "VND";
  const total = timesheets.reduce((sum, x) => sum + x.amount, 0);
  const minutes = timesheets.reduce((sum, x) => sum + x.paidMinutes, 0);
  const late = timesheets.filter((x) => x.lateMinutes > 0).length;

  return (
    <div className="space-y-8">
      <PageHeader title={session("greeting", { name: me.displayName })} description={t("subtitle")} />
      <TeachOnboarding />

      <Fade inView className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
        <StatCard label={ts("sessionsLabel")} count={timesheets.length} icon={CalendarCheck2} hint={ts("thisMonth")} />
        <StatCard label={ts("minutesLabel")} count={minutes} icon={Clock} tone="success" hint={ts("minutesUnit")} />
        <StatCard
          label={ts("amountLabel")}
          value={format.number(total, { style: "currency", currency })}
          icon={Banknote}
          tone="highlight"
          hint={ts("estimate")}
        />
        <StatCard label={ts("lateLabel")} count={late} icon={Receipt} tone={late > 0 ? "warning" : "muted"} hint={ts("lateHint")} />
      </Fade>

      <div className="grid gap-6 lg:grid-cols-[1.4fr_1fr]">
        <SessionList base="/teach" canJoin canTakeAttendance sessions={sessions} title={schedule("teaching")} />
        <Panel title={ts("title")} bodyClassName="p-0">
          {timesheets.length === 0 ? (
            <div className="p-5">
              <EmptyState icon={Receipt} title={ts("empty")} />
            </div>
          ) : (
            <ul className="divide-y">
              {timesheets.map((x) => (
                <li key={x.sessionId} className="flex items-center justify-between gap-3 px-5 py-3 text-sm">
                  <div className="space-y-0.5">
                    <p className="font-medium tabular-nums">
                      {format.dateTime(new Date(`${x.localDate}T00:00:00Z`), { weekday: "short", day: "2-digit", month: "2-digit", timeZone: "UTC" })}
                    </p>
                    <p className="text-muted-foreground">
                      {ts("minutesValue", { minutes: x.paidMinutes })}
                      {x.lateMinutes > 0 && <span className="text-warning-foreground dark:text-warning">{ts("late", { minutes: x.lateMinutes })}</span>}
                    </p>
                  </div>
                  <div className="flex flex-col items-end gap-1">
                    <span className="font-medium tabular-nums">
                      {x.currency ? format.number(x.amount, { style: "currency", currency: x.currency }) : ts("noRate")}
                    </span>
                    <StatusBadge tone={statusTone[x.status] ?? "muted"}>{ts(`status.${x.status}`)}</StatusBadge>
                  </div>
                </li>
              ))}
            </ul>
          )}
        </Panel>
      </div>
    </div>
  );
}
