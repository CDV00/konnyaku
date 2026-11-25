import Link from "next/link";
import { getFormatter, getTranslations } from "next-intl/server";
import { CalendarClock, CalendarPlus, Hourglass, MapPin, Repeat, Video } from "lucide-react";
import { apiFetch, requireMe } from "@konnyaku/session";
import { EmptyState, PageHeader, Panel, StatusBadge, type Tone } from "@konnyaku/ui/kk/blocks";
import { BookingButton } from "../booking-action";

export type Booking = {
  id: string;
  sessionId: string;
  classCode: string;
  studentName: string;
  studentId: string;
  sequenceNo: number;
  startsAt: string;
  endsAt: string;
  deliveryMode: string;
  sessionStatus: string;
  mode: "Online" | "Offline";
  kind: "Regular" | "Makeup" | "Flexible" | "Trial";
  status: "Booked" | "Postponed" | "Cancelled" | "Attended" | "NoShow";
  postponeDeadline: string;
  modeSwitchDeadline: string;
  canPostpone: boolean;
  canCancel: boolean;
  canSwitchMode: boolean;
  otherModeFree: number;
};

type Entitlement = {
  id: string;
  studentName: string;
  classCode: string;
  sequenceNo: number;
  sourceStartsAt: string;
  expiresAt: string;
  state: "Open" | "Used" | "Expired" | "Declined";
  requestedAt: string | null;
  usedBy: { classCode: string; startsAt: string } | null;
};

type WaitlistEntry = {
  id: string;
  classCode: string;
  sequenceNo: number;
  startsAt: string;
  status: "Waiting" | "Offered";
  offerExpiresAt: string | null;
};

const statusTone: Record<Booking["status"], Tone> = {
  Booked: "primary",
  Postponed: "warning",
  Cancelled: "muted",
  Attended: "success",
  NoShow: "destructive",
};

const stateTone: Record<Entitlement["state"], Tone> = { Open: "success", Used: "primary", Expired: "muted", Declined: "muted" };

export default async function SchedulePage() {
  const { token } = await requireMe("learn");
  const [bookingsResponse, makeupsResponse, waitlistResponse, flexibleResponse] = await Promise.all([
    apiFetch("/api/scheduling/my/bookings", { token }),
    apiFetch("/api/scheduling/my/makeups", { token }),
    apiFetch("/api/scheduling/my/waitlist", { token }),
    apiFetch("/api/scheduling/my/flexible", { token }),
  ]);
  const bookings = bookingsResponse.ok ? ((await bookingsResponse.json()) as Booking[]) : [];
  const makeups = makeupsResponse.ok ? ((await makeupsResponse.json()) as Entitlement[]) : [];
  const waitlist = waitlistResponse.ok ? ((await waitlistResponse.json()) as WaitlistEntry[]) : [];
  const flexible = flexibleResponse.ok ? ((await flexibleResponse.json()) as { classes: unknown[] }).classes.length > 0 : false;
  const t = await getTranslations("booking");
  const format = await getFormatter();
  const when = (iso: string) => format.dateTime(new Date(iso), { weekday: "short", day: "2-digit", month: "2-digit", hour: "2-digit", minute: "2-digit" });
  const day = (iso: string) => format.dateTime(new Date(iso), { day: "2-digit", month: "2-digit", year: "numeric" });
  const manyStudents = new Set(bookings.map((b) => b.studentId)).size > 1;
  const offers = waitlist.filter((w) => w.status === "Offered");
  const waiting = waitlist.filter((w) => w.status === "Waiting");

  return (
    <div className="space-y-6">
      <PageHeader
        title={t("title")}
        description={t("description")}
        actions={flexible ? (
          <Link
            href="/learn/flexible"
            className="inline-flex h-9 items-center gap-1.5 rounded-md border bg-background px-3 text-sm font-medium shadow-xs transition-colors hover:bg-accent"
          >
            <CalendarPlus className="size-4" />
            {t("flexibleLink")}
          </Link>
        ) : undefined}
      />

      {offers.length > 0 && (
        <Panel title={t("offers")} bodyClassName="p-0">
          <ul className="divide-y">
            {offers.map((o) => (
              <li key={o.id} className="flex flex-wrap items-center justify-between gap-3 px-5 py-4">
                <p className="text-sm">
                  {t("offerHint", { number: o.sequenceNo, code: o.classCode, date: when(o.startsAt), until: when(o.offerExpiresAt ?? o.startsAt) })}
                </p>
                <div className="flex gap-2">
                  <BookingButton path={`waitlist/${o.id}/accept`} label={t("accept")} variant="default" />
                  <BookingButton path={`waitlist/${o.id}/withdraw`} label={t("withdraw")} />
                </div>
              </li>
            ))}
          </ul>
        </Panel>
      )}

      <div className="grid gap-6 xl:grid-cols-[1.7fr_1fr]">
        <Panel title={t("upcoming")} bodyClassName="p-0" className="order-last xl:order-first">
          {bookings.length === 0 ? (
            <div className="p-5">
              <EmptyState icon={CalendarClock} title={t("empty")} />
            </div>
          ) : (
            <ul className="divide-y">
              {bookings.map((b) => {
                const other = b.mode === "Online" ? "Offline" : "Online";
                const open = b.status === "Booked" && b.sessionStatus !== "Cancelled";
                return (
                  <li key={b.id} className="flex flex-col gap-3 px-5 py-4 lg:flex-row lg:items-start lg:justify-between">
                    <div className="min-w-0 space-y-1">
                      <p className="font-medium">{t("classLesson", { code: b.classCode, number: b.sequenceNo })}</p>
                      <p className="flex flex-wrap items-center gap-x-3 gap-y-1 text-sm text-muted-foreground">
                        <span className="tabular-nums">{when(b.startsAt)}</span>
                        <span className="inline-flex items-center gap-1">
                          {b.mode === "Offline" ? <MapPin className="size-3.5" /> : <Video className="size-3.5" />}
                          {t(`mode.${b.mode}`)}
                        </span>
                        {manyStudents && <span>{t("forStudent", { name: b.studentName })}</span>}
                      </p>
                      <div className="flex flex-wrap gap-1.5">
                        <StatusBadge tone={statusTone[b.status]}>{t(`status.${b.status}`)}</StatusBadge>
                        {b.kind !== "Regular" && <StatusBadge tone="highlight">{t(`kind.${b.kind}`)}</StatusBadge>}
                      </div>
                      {open && b.kind === "Regular" && (
                        <p className="text-xs text-muted-foreground">
                          {b.canPostpone ? t("postponeUntil", { deadline: when(b.postponeDeadline) }) : t("postponeClosed", { deadline: when(b.postponeDeadline) })}
                        </p>
                      )}
                      {b.canSwitchMode && (
                        <p className="text-xs text-muted-foreground">
                          {t("switchHint", { deadline: when(b.modeSwitchDeadline), count: b.otherModeFree })}
                        </p>
                      )}
                    </div>
                    <div className="flex flex-wrap items-start gap-2">
                      {b.canPostpone && <BookingButton path={`bookings/${b.id}/postpone`} label={t("postpone")} confirm={t("postponeConfirm")} />}
                      {b.canCancel && <BookingButton path={`bookings/${b.id}/cancel`} label={t("cancel")} confirm={t("cancelConfirm")} />}
                      {b.canSwitchMode && (
                        <BookingButton path={`bookings/${b.id}/mode`} body={{ mode: other }} label={t("switchTo", { mode: t(`mode.${other}`) })} />
                      )}
                    </div>
                  </li>
                );
              })}
            </ul>
          )}
        </Panel>

        <div className="space-y-6">
          <Panel title={t("makeups")} bodyClassName="p-0">
            {makeups.length === 0 ? (
              <div className="p-5">
                <EmptyState icon={Repeat} title={t("makeupsEmpty")} />
              </div>
            ) : (
              <ul className="divide-y">
                {makeups.map((m) => (
                  <li key={m.id} className="space-y-2 px-5 py-4">
                    <div className="flex items-start justify-between gap-3">
                      <p className="text-sm font-medium">
                        {t("makeupFor", { number: m.sequenceNo, code: m.classCode, date: day(m.sourceStartsAt) })}
                      </p>
                      <StatusBadge tone={stateTone[m.state]}>{t(`state.${m.state}`)}</StatusBadge>
                    </div>
                    <p className="text-xs text-muted-foreground">
                      {m.usedBy ? t("usedAt", { code: m.usedBy.classCode, date: when(m.usedBy.startsAt) }) : t("expires", { date: day(m.expiresAt) })}
                      {manyStudents && ` · ${t("forStudent", { name: m.studentName })}`}
                    </p>
                    {m.state === "Open" && (
                      <div className="flex flex-wrap items-center gap-2">
                        <Link
                          href={`/learn/makeups/${m.id}`}
                          className="inline-flex h-8 items-center gap-1.5 rounded-md bg-primary px-3 text-sm font-medium text-primary-foreground shadow-xs transition-colors hover:bg-primary/90"
                        >
                          {t("pick")}
                        </Link>
                        {m.requestedAt && <StatusBadge tone="warning">{t("requested")}</StatusBadge>}
                      </div>
                    )}
                  </li>
                ))}
              </ul>
            )}
          </Panel>

          {waiting.length > 0 && (
            <Panel title={t("waiting")} bodyClassName="p-0">
              <ul className="divide-y">
                {waiting.map((w) => (
                  <li key={w.id} className="flex flex-wrap items-center justify-between gap-3 px-5 py-4">
                    <p className="flex items-start gap-2 text-sm">
                      <Hourglass className="mt-0.5 size-4 shrink-0 text-muted-foreground" />
                      {t("waitingHint", { number: w.sequenceNo, code: w.classCode, date: when(w.startsAt) })}
                    </p>
                    <BookingButton path={`waitlist/${w.id}/withdraw`} label={t("leaveWaitlist")} />
                  </li>
                ))}
              </ul>
            </Panel>
          )}
        </div>
      </div>
    </div>
  );
}
