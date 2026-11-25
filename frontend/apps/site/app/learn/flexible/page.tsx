import Link from "next/link";
import { getFormatter, getTranslations } from "next-intl/server";
import { CalendarPlus } from "lucide-react";
import { apiFetch, requireMe } from "@konnyaku/session";
import { EmptyState, PageHeader, Panel, StatusBadge } from "@konnyaku/ui/kk/blocks";
import { BookingButton } from "../booking-action";

type FlexibleClass = { classId: string; classCode: string; studentId: string; studentName: string; creditsLeft: number | null; bookedAhead: number };
type FlexibleSession = {
  sessionId: string;
  classId: string;
  sequenceNo: number;
  startsAt: string;
  endsAt: string;
  deliveryMode: "Online" | "Offline" | "Hybrid";
  onlineFree: number;
  offlineFree: number;
  bookingId: string | null;
  bookingStatus: string | null;
  bookingMode: "Online" | "Offline" | null;
  waitlisted: boolean;
};

const modesOf = (deliveryMode: FlexibleSession["deliveryMode"]) =>
  deliveryMode === "Online" ? (["Online"] as const) : deliveryMode === "Offline" ? (["Offline"] as const) : (["Offline", "Online"] as const);

export default async function FlexiblePage() {
  const { token } = await requireMe("learn");
  const response = await apiFetch("/api/scheduling/my/flexible", { token });
  const data = response.ok
    ? ((await response.json()) as { classes: FlexibleClass[]; sessions: FlexibleSession[] })
    : { classes: [], sessions: [] };
  const t = await getTranslations("booking");
  const format = await getFormatter();
  const when = (iso: string) => format.dateTime(new Date(iso), { weekday: "short", day: "2-digit", month: "2-digit", hour: "2-digit", minute: "2-digit" });

  return (
    <div className="space-y-6">
      <Link href="/learn/schedule" className="text-sm text-muted-foreground hover:text-foreground">{t("options.back")}</Link>
      <PageHeader title={t("flexible.title")} description={t("flexible.description")} />
      {data.classes.length === 0 ? (
        <EmptyState icon={CalendarPlus} title={t("flexible.empty")} />
      ) : (
        data.classes.map((c) => {
          const sessions = data.sessions.filter((s) => s.classId === c.classId);
          return (
            <Panel
              key={`${c.classId}-${c.studentId}`}
              title={c.classCode}
              description={[
                t("flexible.summary", { name: c.studentName, booked: c.bookedAhead }),
                ...(c.creditsLeft === null ? [] : [t("flexible.credits", { credits: c.creditsLeft })]),
              ].join(" · ")}
              bodyClassName="p-0"
            >
              {sessions.length === 0 ? (
                <div className="p-5">
                  <EmptyState icon={CalendarPlus} title={t("flexible.noSessions")} />
                </div>
              ) : (
                <ul className="divide-y">
                  {sessions.map((s) => {
                    const mine = s.bookingStatus === "Booked" || s.bookingStatus === "Attended";
                    const modes = modesOf(s.deliveryMode).filter((m) => (m === "Online" ? s.onlineFree : s.offlineFree) > 0);
                    return (
                      <li key={s.sessionId} className="flex flex-col gap-3 px-5 py-4 sm:flex-row sm:items-center sm:justify-between">
                        <div className="space-y-1">
                          <p className="font-medium tabular-nums">{when(s.startsAt)}</p>
                          <p className="text-xs text-muted-foreground">
                            {t("lesson", { number: s.sequenceNo })}
                            {" · "}
                            {s.deliveryMode === "Hybrid"
                              ? t("flexible.free", { online: s.onlineFree, offline: s.offlineFree })
                              : t("flexible.freeOne", { count: s.deliveryMode === "Online" ? s.onlineFree : s.offlineFree })}
                          </p>
                        </div>
                        <div className="flex flex-wrap items-center gap-2">
                          {mine && s.bookingMode ? (
                            <>
                              <StatusBadge tone="success">{t("flexible.booked", { mode: t(`mode.${s.bookingMode}`) })}</StatusBadge>
                              {s.bookingStatus === "Booked" && s.bookingId && (
                                <BookingButton path={`bookings/${s.bookingId}/cancel`} label={t("cancel")} confirm={t("cancelConfirm")} />
                              )}
                            </>
                          ) : s.waitlisted ? (
                            <StatusBadge tone="warning">{t("flexible.waitlisted")}</StatusBadge>
                          ) : modes.length === 0 ? (
                            <BookingButton
                              path={`sessions/${s.sessionId}/waitlist`}
                              body={{ mode: modesOf(s.deliveryMode)[0], studentId: c.studentId }}
                              label={t("flexible.waitlist")}
                            />
                          ) : (
                            modes.map((mode) => (
                              <BookingButton
                                key={mode}
                                path={`sessions/${s.sessionId}/book`}
                                body={{ mode, studentId: c.studentId }}
                                variant="default"
                                label={modes.length > 1 ? t("options.bookMode", { mode: t(`mode.${mode}`) }) : t("flexible.book")}
                              />
                            ))
                          )}
                        </div>
                      </li>
                    );
                  })}
                </ul>
              )}
            </Panel>
          );
        })
      )}
    </div>
  );
}
