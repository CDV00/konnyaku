import Link from "next/link";
import { notFound } from "next/navigation";
import { getFormatter, getTranslations } from "next-intl/server";
import { CalendarSearch, MapPin, Video } from "lucide-react";
import { apiFetch, requireMe } from "@konnyaku/session";
import { EmptyState, PageHeader, Panel, StatusBadge } from "@konnyaku/ui/kk/blocks";
import { BookingButton, MakeupRequestForm } from "../../booking-action";

type Option = {
  sessionId: string;
  classCode: string;
  sequenceNo: number;
  startsAt: string;
  deliveryMode: "Online" | "Offline" | "Hybrid";
  onlineFree: number;
  offlineFree: number;
  makeupFree: number;
};

type Entitlement = { id: string; state: string; expiresAt: string; requestedAt: string | null };

const modesOf = (deliveryMode: Option["deliveryMode"]) =>
  deliveryMode === "Online" ? (["Online"] as const) : deliveryMode === "Offline" ? (["Offline"] as const) : (["Offline", "Online"] as const);

export default async function MakeupOptionsPage({ params }: { params: Promise<{ id: string }> }) {
  const { token } = await requireMe("learn");
  const { id } = await params;
  const [rightsResponse, optionsResponse] = await Promise.all([
    apiFetch("/api/scheduling/my/makeups", { token }),
    apiFetch(`/api/scheduling/my/makeups/${id}/options`, { token }),
  ]);
  const entitlement = (rightsResponse.ok ? ((await rightsResponse.json()) as Entitlement[]) : []).find((e) => e.id === id);
  if (!entitlement || !optionsResponse.ok) notFound();
  const options = (await optionsResponse.json()) as Option[];
  const t = await getTranslations("booking");
  const format = await getFormatter();
  const when = (iso: string) => format.dateTime(new Date(iso), { weekday: "short", day: "2-digit", month: "2-digit", hour: "2-digit", minute: "2-digit" });

  return (
    <div className="space-y-6">
      <Link href="/learn/schedule" className="text-sm text-muted-foreground hover:text-foreground">{t("options.back")}</Link>
      <PageHeader
        title={t("options.title")}
        description={t("options.description", { date: format.dateTime(new Date(entitlement.expiresAt), { dateStyle: "medium" }) })}
      />
      {entitlement.state !== "Open" ? (
        <EmptyState icon={CalendarSearch} title={t("options.notOpen")} />
      ) : (
        <>
          <Panel bodyClassName="p-0">
            {options.length === 0 ? (
              <div className="p-5">
                <EmptyState icon={CalendarSearch} title={t("options.empty")} />
              </div>
            ) : (
              <ul className="divide-y">
                {options.map((o) => {
                  const modes = modesOf(o.deliveryMode).filter((m) => (m === "Online" ? o.onlineFree : o.offlineFree) > 0);
                  const full = o.makeupFree === 0 || modes.length === 0;
                  return (
                    <li key={o.sessionId} className="flex flex-col gap-3 px-5 py-4 lg:flex-row lg:items-center lg:justify-between">
                      <div className="space-y-1">
                        <p className="font-medium">{t("classLesson", { code: o.classCode, number: o.sequenceNo })}</p>
                        <p className="flex flex-wrap items-center gap-x-3 text-sm text-muted-foreground">
                          <span className="tabular-nums">{when(o.startsAt)}</span>
                          <span className="inline-flex items-center gap-1">
                            {o.deliveryMode === "Offline" ? <MapPin className="size-3.5" /> : <Video className="size-3.5" />}
                            {o.deliveryMode === "Hybrid" ? `${t("mode.Offline")} / ${t("mode.Online")}` : t(`mode.${o.deliveryMode}`)}
                          </span>
                        </p>
                        <p className="text-xs text-muted-foreground">
                          {t("options.seats", { online: o.onlineFree, offline: o.offlineFree, makeup: o.makeupFree })}
                        </p>
                      </div>
                      <div className="flex flex-wrap items-start gap-2">
                        {full ? (
                          <>
                            <StatusBadge tone="muted">{t("options.full")}</StatusBadge>
                            <BookingButton
                              path={`sessions/${o.sessionId}/waitlist`}
                              body={{ entitlementId: id, mode: modesOf(o.deliveryMode)[0] }}
                              label={t("options.waitlist")}
                            />
                          </>
                        ) : (
                          modes.map((mode) => (
                            <BookingButton
                              key={mode}
                              path={`makeups/${id}/book`}
                              body={{ sessionId: o.sessionId, mode }}
                              variant="default"
                              label={modes.length > 1 ? t("options.bookMode", { mode: t(`mode.${mode}`) }) : t("options.book")}
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
          {entitlement.requestedAt ? (
            <StatusBadge tone="warning">{t("requested")}</StatusBadge>
          ) : (
            <Panel title={t("options.request")}>
              <MakeupRequestForm entitlementId={id} label={t("options.request")} noteLabel={t("options.requestNote")} />
            </Panel>
          )}
        </>
      )}
    </div>
  );
}
