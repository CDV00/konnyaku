import Link from "next/link";
import { getTranslations } from "next-intl/server";
import { ArrowLeft } from "lucide-react";
import { apiFetch, requireMe } from "@konnyaku/session";
import { PageHeader } from "@konnyaku/ui/kk/blocks";
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
    <div className="space-y-6">
      <Link href="/teach" className="inline-flex items-center gap-1.5 text-sm text-muted-foreground hover:text-foreground">
        <ArrowLeft className="size-4" />
        {t("back")}
      </Link>
      <PageHeader title={t("title", { number: Number(n ?? 0) })} description={t("intro")} />
      <AttendanceForm sessionId={id} rows={rows} />
    </div>
  );
}
