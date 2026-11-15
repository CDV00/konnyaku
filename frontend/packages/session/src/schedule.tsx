import Link from "next/link";
import { getFormatter, getTranslations } from "next-intl/server";
import { CalendarClock, ClipboardCheck, MapPin, Video } from "lucide-react";
import { EmptyState, Panel, StatusBadge } from "@konnyaku/ui/kk/blocks";

export type SessionView = {
  id: string;
  classId: string;
  sequenceNo: number;
  startsAt: string;
  endsAt: string;
  deliveryMode: "Online" | "Offline" | "Hybrid";
  status: string;
};

/**
 * Upcoming sessions as a date-led list, formatted in the center's time zone (set in each app's i18n config).
 * `canJoin`: a join button when the room is ready (resolved under the app basePath: /learn/join or /teach/join).
 */
export async function SessionList({ sessions, title, canJoin = false, canTakeAttendance = false, limit = 8 }: {
  sessions: SessionView[];
  title: string;
  canJoin?: boolean;
  canTakeAttendance?: boolean;
  /** Only the nearest lessons: a long term would otherwise push everything else off the screen. */
  limit?: number;
}) {
  const t = await getTranslations("schedule");
  const format = await getFormatter();
  return (
    <Panel title={title} bodyClassName="p-0">
      {sessions.length === 0 ? (
        <div className="p-5">
          <EmptyState icon={CalendarClock} title={t("empty")} />
        </div>
      ) : (
        <ul className="divide-y">
          {sessions.slice(0, limit).map((s) => {
            const start = new Date(s.startsAt);
            const ready = s.status === "Provisioned";
            return (
              <li key={s.id} className="flex flex-wrap items-center gap-4 px-5 py-4 transition-colors hover:bg-muted/40">
                <div className="grid w-14 shrink-0 place-items-center rounded-lg border bg-background py-1.5 text-center">
                  <span className="text-[0.65rem] font-semibold tracking-wide text-primary uppercase">
                    {format.dateTime(start, { weekday: "short" })}
                  </span>
                  <span className="text-xl leading-none font-semibold tabular-nums">{format.dateTime(start, { day: "2-digit" })}</span>
                  <span className="text-[0.65rem] text-muted-foreground">{format.dateTime(start, { month: "short" })}</span>
                </div>
                <div className="min-w-0 flex-1 space-y-1">
                  <p className="font-medium">{t("lesson", { number: s.sequenceNo })}</p>
                  <p className="flex flex-wrap items-center gap-x-3 gap-y-1 text-sm text-muted-foreground">
                    <span className="tabular-nums">
                      {format.dateTimeRange(start, new Date(s.endsAt), { hour: "2-digit", minute: "2-digit" })}
                    </span>
                    <span className="inline-flex items-center gap-1">
                      {s.deliveryMode === "Offline" ? <MapPin className="size-3.5" /> : <Video className="size-3.5" />}
                      {t(`mode.${s.deliveryMode}`)}
                    </span>
                  </p>
                </div>
                <div className="flex items-center gap-2">
                  {s.status === "Cancelled" && <StatusBadge tone="destructive">{t("cancelled")}</StatusBadge>}
                  {canJoin && ready && (
                    <Link
                      href={`/join/${s.id}`}
                      prefetch={false}
                      className="inline-flex h-9 items-center gap-1.5 rounded-md bg-primary px-3 text-sm font-medium text-primary-foreground shadow-xs transition-colors hover:bg-primary/90"
                    >
                      <Video className="size-4" />
                      {t("join")}
                    </Link>
                  )}
                  {canTakeAttendance && s.status !== "Cancelled" && (
                    <Link
                      href={`/sessions/${s.id}/attendance?n=${s.sequenceNo}`}
                      className="inline-flex h-9 items-center gap-1.5 rounded-md border bg-background px-3 text-sm font-medium shadow-xs transition-colors hover:bg-accent"
                    >
                      <ClipboardCheck className="size-4" />
                      {t("attendance")}
                    </Link>
                  )}
                </div>
              </li>
            );
          })}
          {sessions.length > limit && (
            <li className="px-5 py-3 text-center text-sm text-muted-foreground">{t("more", { count: sessions.length - limit })}</li>
          )}
        </ul>
      )}
    </Panel>
  );
}
