import { notFound } from "next/navigation";
import { getFormatter, getTranslations } from "next-intl/server";
import { CalendarClock, UserRoundX } from "lucide-react";
import { requireMe } from "@konnyaku/session";
import { Input } from "@konnyaku/ui/components/ui/input";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@konnyaku/ui/components/ui/table";
import { EmptyState, PageHeader, Panel, StatusBadge } from "@konnyaku/ui/kk/blocks";
import { Field, NativeSelect } from "@konnyaku/ui/kk/field";
import { ActionButton, ActionForm, FormSheet } from "../action-form";
import { getAccess, getJson } from "../lib";

type Request = {
  id: string;
  studentName: string;
  classCode: string;
  sequenceNo: number;
  sourceStartsAt: string;
  expiresAt: string;
  requestedAt: string;
  note: string | null;
};

type Option = {
  sessionId: string;
  classCode: string;
  sequenceNo: number;
  startsAt: string;
  deliveryMode: "Online" | "Offline" | "Hybrid";
  onlineFree: number;
  offlineFree: number;
  sameLesson: boolean;
};

type Frequent = { studentId: string; enrollmentId: string; studentName: string; classCode: string; postponed: number; lastPostponedAt: string };

type Settings = {
  postponeDeadlineHours: number;
  maxPostponesPerEnrollment: number;
  entitlementDays: number;
  modeSwitchDeadlineHours: number;
  makeupSeatsPerSession: number;
  waitlistHoldHours: number;
};

const settingKeys = [
  "postponeDeadlineHours",
  "maxPostponesPerEnrollment",
  "entitlementDays",
  "modeSwitchDeadlineHours",
  "makeupSeatsPerSession",
  "waitlistHoldHours",
] as const;

const days = 60;
const minimum = 2;

export default async function MakeupsPage() {
  const { me, token } = await requireMe("staff");
  const access = await getAccess(token);
  if (!access.any("scheduling.schedules")) notFound();
  const [requests, frequent, settings] = await Promise.all([
    getJson<Request[]>("/api/scheduling/makeups/requests", token, []),
    getJson<Frequent[]>(`/api/scheduling/postpones/frequent?days=${days}&min=${minimum}`, token, []),
    getJson<Settings | null>("/api/scheduling/settings/booking", token, null),
  ]);
  const rows = await Promise.all(
    requests.map(async (r) => ({ ...r, options: await getJson<Option[]>(`/api/scheduling/makeups/${r.id}/options`, token, []) })),
  );
  const t = await getTranslations("makeups");
  const admin = await getTranslations("admin");
  const booking = await getTranslations("booking");
  const format = await getFormatter();
  const day = (iso: string) => format.dateTime(new Date(iso), { day: "2-digit", month: "2-digit", year: "numeric" });
  const when = (iso: string) => format.dateTime(new Date(iso), { weekday: "short", day: "2-digit", month: "2-digit", hour: "2-digit", minute: "2-digit" });
  const isAdmin = me.roles.includes("TenantAdmin");

  return (
    <div className="space-y-6">
      <PageHeader title={t("title")} description={t("description")} />

      <Panel title={t("requests")} bodyClassName="p-0">
        {requests.length === 0 ? (
          <div className="p-5">
            <EmptyState icon={CalendarClock} title={t("requestsEmpty")} />
          </div>
        ) : (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>{t("student")}</TableHead>
                <TableHead className="hidden sm:table-cell">{t("missed")}</TableHead>
                <TableHead className="hidden xl:table-cell">{t("expires")}</TableHead>
                <TableHead className="hidden 2xl:table-cell">{t("note")}</TableHead>
                <TableHead />
              </TableRow>
            </TableHeader>
            <TableBody>
              {rows.map((r) => (
                <TableRow key={r.id}>
                  <TableCell>
                    <p className="font-medium">{r.studentName}</p>
                    <p className="text-xs text-muted-foreground">{r.classCode}</p>
                    <p className="text-xs text-muted-foreground sm:hidden">{t("missedValue", { number: r.sequenceNo, date: day(r.sourceStartsAt) })}</p>
                  </TableCell>
                  <TableCell className="hidden sm:table-cell">{t("missedValue", { number: r.sequenceNo, date: day(r.sourceStartsAt) })}</TableCell>
                  <TableCell className="hidden xl:table-cell">{day(r.expiresAt)}</TableCell>
                  <TableCell className="hidden max-w-64 truncate 2xl:table-cell">{r.note}</TableCell>
                  <TableCell>
                    <div className="flex flex-col items-end gap-2 lg:flex-row lg:justify-end">
                      <FormSheet triggerLabel={t("place")} title={t("placeTitle", { name: r.studentName })} description={t("placeHint")} icon={false}>
                        {r.options.length === 0 ? (
                          <EmptyState icon={CalendarClock} title={t("noOptions")} />
                        ) : (
                          <ActionForm api={`scheduling/makeups/${r.id}/place`} submitLabel={t("place")}>
                            <Field label={t("session")}>
                              <NativeSelect name="sessionId" required>
                                {r.options.map((o) => (
                                  <option key={o.sessionId} value={o.sessionId}>
                                    {t("sessionOption", { code: o.classCode, number: o.sequenceNo, date: when(o.startsAt), same: String(o.sameLesson) })}
                                  </option>
                                ))}
                              </NativeSelect>
                            </Field>
                            <Field label={t("mode")}>
                              <NativeSelect name="mode" defaultValue="">
                                <option value="">{t("modeAuto")}</option>
                                <option value="Offline">{booking("mode.Offline")}</option>
                                <option value="Online">{booking("mode.Online")}</option>
                              </NativeSelect>
                            </Field>
                          </ActionForm>
                        )}
                      </FormSheet>
                      <ActionButton api={`scheduling/makeups/${r.id}/decline`} label={t("decline")} confirm={t("declineConfirm")} />
                    </div>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </Panel>

      <div className="grid gap-6 xl:grid-cols-[1.6fr_1fr]">
        <Panel title={t("frequent")} description={t("frequentHint", { min: minimum, days })} bodyClassName="p-0">
          {frequent.length === 0 ? (
            <div className="p-5">
              <EmptyState icon={UserRoundX} title={t("frequentEmpty")} />
            </div>
          ) : (
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>{t("student")}</TableHead>
                  <TableHead>{t("class")}</TableHead>
                  <TableHead>{t("postponed")}</TableHead>
                  <TableHead className="hidden sm:table-cell">{t("last")}</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {frequent.map((f) => (
                  <TableRow key={f.enrollmentId}>
                    <TableCell className="font-medium">{f.studentName}</TableCell>
                    <TableCell>{f.classCode}</TableCell>
                    <TableCell>
                      <StatusBadge tone={f.postponed >= 3 ? "destructive" : "warning"}>{t("settingsValue", { value: f.postponed })}</StatusBadge>
                    </TableCell>
                    <TableCell className="hidden sm:table-cell">{day(f.lastPostponedAt)}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
        </Panel>

        {settings && (
          <Panel
            title={t("settings")}
            description={t("settingsHint")}
            action={isAdmin ? (
              <FormSheet triggerLabel={t("settingsEdit")} title={t("settings")} description={t("settingsHint")} icon={false}>
                <ActionForm api="scheduling/settings/booking" method="PUT" submitLabel={admin("save")}>
                  {settingKeys.map((key) => (
                    <Field key={key} label={t(key)}>
                      <Input name={key} type="number" min={0} required defaultValue={settings[key]} />
                    </Field>
                  ))}
                </ActionForm>
              </FormSheet>
            ) : undefined}
            bodyClassName="p-0"
          >
            <dl className="divide-y">
              {settingKeys.map((key) => (
                <div key={key} className="flex items-center justify-between gap-4 px-5 py-3 text-sm">
                  <dt className="text-muted-foreground">{t(key)}</dt>
                  <dd className="font-medium tabular-nums">{t("settingsValue", { value: settings[key] })}</dd>
                </div>
              ))}
            </dl>
          </Panel>
        )}
      </div>
    </div>
  );
}
