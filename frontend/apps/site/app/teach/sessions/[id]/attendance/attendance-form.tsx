"use client";

import { useState, type FormEvent } from "react";
import { useTranslations } from "next-intl";
import { AnimatePresence, motion } from "motion/react";
import { CheckCircle2, MapPin, Users, Video } from "lucide-react";
import { Button } from "@konnyaku/ui/components/animate-ui/components/buttons/button";
import { ToggleGroup, ToggleGroupItem } from "@konnyaku/ui/components/animate-ui/components/radix/toggle-group";
import { EmptyState, FormMessage, StatusBadge } from "@konnyaku/ui/kk/blocks";

export type AttendanceRow = {
  enrollmentId: string;
  studentName: string;
  status: string;
  presentMinutes: number;
  confirmed: boolean;
  /** Booking kind: Regular (own class), Makeup, Flexible or Trial. */
  kind?: string;
  /** Seat: Online or Offline (null for rows recorded before bookings existed). */
  mode?: string | null;
};

const statuses = ["Present", "Late", "LeftEarly", "Absent", "ExcusedAbsent"] as const;
/** A make-up learner who came is recorded as Makeup (one lesson from their own package). */
const makeupStatuses = ["Makeup", "Absent", "ExcusedAbsent"] as const;

const dot: Record<string, string> = {
  Present: "bg-success",
  Late: "bg-warning",
  LeftEarly: "bg-warning",
  Absent: "bg-destructive",
  ExcusedAbsent: "bg-muted-foreground",
  Makeup: "bg-success",
};

export function AttendanceForm({ sessionId, rows }: { sessionId: string; rows: AttendanceRow[] }) {
  const t = useTranslations("attendance");
  const [confirmed, setConfirmed] = useState(rows.some((r) => r.confirmed));
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState(false);
  const [picked, setPicked] = useState<Record<string, string>>(() => Object.fromEntries(rows.map((r) => [r.enrollmentId, r.status])));

  async function onSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setPending(true);
    setError(null);
    const overrides = rows.map((r) => ({ enrollmentId: r.enrollmentId, status: picked[r.enrollmentId] ?? r.status }));
    const response = await fetch(`/teach/api/attendance/${sessionId}`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ overrides }),
    });
    setPending(false);
    if (response.ok) {
      setConfirmed(true);
      return;
    }
    const body = (await response.json().catch(() => ({}))) as { code?: string };
    const key = `errors.${(body.code ?? "unknown").replace(".", "_")}`;
    setError(t.has(key) ? t(key) : t("errors.unknown"));
  }

  const present = Object.values(picked).filter((s) => s === "Present" || s === "Late" || s === "LeftEarly" || s === "Makeup").length;

  return (
    <AnimatePresence mode="wait" initial={false}>
      {confirmed ? (
        <motion.div
          key="done"
          role="status"
          initial={{ opacity: 0, y: 8 }}
          animate={{ opacity: 1, y: 0 }}
          className="flex items-center gap-3 rounded-xl border border-success/30 bg-success/10 px-4 py-3 text-success"
        >
          <CheckCircle2 className="size-5 shrink-0" />
          <span className="text-sm font-medium">{t("confirmed")}</span>
        </motion.div>
      ) : (
        <motion.form key="form" method="post" onSubmit={onSubmit} exit={{ opacity: 0 }} className="space-y-4">
          {rows.length === 0 ? (
            <EmptyState icon={Users} title={t("empty")} />
          ) : (
            <>
              <p className="text-sm text-muted-foreground">{t("summary", { present, total: rows.length })}</p>
              <ul className="divide-y rounded-xl border bg-card">
                {rows.map((r) => (
                  <li key={r.enrollmentId} className="flex flex-col gap-3 px-4 py-3 lg:flex-row lg:items-center lg:justify-between">
                    <div className="flex items-center gap-3">
                      <span className={`size-2.5 shrink-0 rounded-full ${dot[picked[r.enrollmentId] ?? r.status] ?? "bg-muted"}`} aria-hidden="true" />
                      <div className="space-y-1">
                        <p className="flex flex-wrap items-center gap-2 font-medium">
                          {r.studentName}
                          {r.kind && r.kind !== "Regular" && <StatusBadge tone="highlight">{t(`kinds.${r.kind as "Makeup"}`)}</StatusBadge>}
                        </p>
                        <p className="flex flex-wrap items-center gap-x-3 text-xs text-muted-foreground">
                          {r.mode && (
                            <span className="inline-flex items-center gap-1">
                              {r.mode === "Offline" ? <MapPin className="size-3" /> : <Video className="size-3" />}
                              {t(`modes.${r.mode as "Online"}`)}
                            </span>
                          )}
                          <span>{t("presentMinutes", { minutes: r.presentMinutes })}</span>
                        </p>
                      </div>
                    </div>
                    <ToggleGroup
                      type="single"
                      variant="outline"
                      size="sm"
                      value={picked[r.enrollmentId] ?? r.status}
                      onValueChange={(value: string) => value && setPicked((p) => ({ ...p, [r.enrollmentId]: value }))}
                      className="flex-wrap"
                      aria-label={t("status")}
                    >
                      {(r.kind === "Makeup" ? makeupStatuses : statuses).map((s) => (
                        <ToggleGroupItem key={s} value={s} className="px-2.5 text-xs">{t(`statuses.${s}`)}</ToggleGroupItem>
                      ))}
                    </ToggleGroup>
                  </li>
                ))}
              </ul>
            </>
          )}
          {error && <FormMessage>{error}</FormMessage>}
          <Button type="submit" size="lg" disabled={pending}>
            <CheckCircle2 />
            {t("confirm")}
          </Button>
        </motion.form>
      )}
    </AnimatePresence>
  );
}
