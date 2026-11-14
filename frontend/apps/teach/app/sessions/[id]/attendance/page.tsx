import Link from "next/link";
import { getTranslations } from "next-intl/server";
import { apiFetch, requireMe } from "@konnyaku/session";
import { AttendanceForm, type AttendanceRow } from "./attendance-form";

export default async function AttendancePage({ params, searchParams }: {
  params: Promise<{ id: string }>;
  searchParams: Promise<{ n?: string }>;
}) {
  const { token } = await requireMe("staff");
  const { id } = await params;
  const { n } = await searchParams;
  const t = await getTranslations("attendance");
  const response = await apiFetch(`/api/learning/sessions/${id}/attendance`, { token });
  const rows = response.ok ? ((await response.json()) as AttendanceRow[]) : [];
  return (
    <main style={{ fontFamily: "system-ui", padding: 24 }}>
      <h1>{t("title", { number: Number(n ?? 0) })}</h1>
      <AttendanceForm sessionId={id} rows={rows} />
      <p><Link href="/">{t("back")}</Link></p>
    </main>
  );
}
