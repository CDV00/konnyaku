"use client";

import { useState, type FormEvent } from "react";
import { useTranslations } from "next-intl";

export type AttendanceRow = {
  enrollmentId: string;
  studentName: string;
  status: string;
  presentMinutes: number;
  confirmed: boolean;
};

const statuses = ["Present", "Late", "LeftEarly", "Absent", "ExcusedAbsent"] as const;

export function AttendanceForm({ sessionId, rows }: { sessionId: string; rows: AttendanceRow[] }) {
  const t = useTranslations("attendance");
  const [confirmed, setConfirmed] = useState(rows.some((r) => r.confirmed));
  const [error, setError] = useState<string | null>(null);

  async function onSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = new FormData(event.currentTarget);
    const overrides = rows.map((r) => ({ enrollmentId: r.enrollmentId, status: String(form.get(r.enrollmentId) ?? r.status) }));
    const response = await fetch(`/teach/api/attendance/${sessionId}`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ overrides }),
    });
    if (response.ok) {
      setConfirmed(true);
      return;
    }
    const body = (await response.json().catch(() => ({}))) as { code?: string };
    const key = `errors.${(body.code ?? "unknown").replace(".", "_")}`;
    setError(t.has(key) ? t(key) : t("errors.unknown"));
  }

  if (confirmed) return <p role="status">{t("confirmed")}</p>;

  return (
    <form method="post" onSubmit={onSubmit}>
      {rows.length === 0 ? (
        <p>{t("empty")}</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>{t("student")}</th>
              <th>{t("minutes")}</th>
              <th>{t("status")}</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((r) => (
              <tr key={r.enrollmentId}>
                <td>{r.studentName}</td>
                <td>{r.presentMinutes}</td>
                <td>
                  <select name={r.enrollmentId} defaultValue={r.status}>
                    {statuses.map((s) => (
                      <option key={s} value={s}>{t(`statuses.${s}`)}</option>
                    ))}
                  </select>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {error && <p role="alert">{error}</p>}
      <button type="submit">{t("confirm")}</button>
    </form>
  );
}
