import Link from "next/link";
import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { ArrowLeft } from "lucide-react";
import { apiFetch, requireMe } from "@konnyaku/session";
import type { Bank, Framework, PaperDetail } from "@/assessment/types";
import { AssessmentTabs } from "../../assessment-tabs";
import type { Language } from "../../authoring";
import { PaperBuilder } from "./paper-builder";

/** Paper builder; `/teach/assessments/papers/new` creates one (the API refuses an empty paper, so nothing is saved before). */
export default async function PaperPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const isNew = id === "new";
  if (!isNew && !/^[0-9a-f-]{36}$/i.test(id)) notFound();
  const { token } = await requireMe("staff");
  const [paperResponse, banksResponse, languagesResponse, frameworksResponse] = await Promise.all([
    isNew ? Promise.resolve(null) : apiFetch(`/api/assessment/papers/${id}`, { token }),
    apiFetch("/api/assessment/banks", { token }),
    apiFetch("/api/catalog/languages", { token }),
    apiFetch("/api/catalog/frameworks", { token }),
  ]);
  if (paperResponse && !paperResponse.ok) notFound();
  const detail = paperResponse ? ((await paperResponse.json()) as PaperDetail) : null;
  const banks = banksResponse.ok ? ((await banksResponse.json()) as Bank[]) : [];
  const languages = languagesResponse.ok ? ((await languagesResponse.json()) as Language[]) : [];
  const frameworks = frameworksResponse.ok ? ((await frameworksResponse.json()) as Framework[]) : [];
  const t = await getTranslations("assessmentAuthoring");

  return (
    <div className="space-y-6">
      <Link href="/teach/assessments/papers" className="inline-flex items-center gap-1 text-sm text-muted-foreground hover:text-foreground">
        <ArrowLeft className="size-4" />
        {t("papers.back")}
      </Link>
      <AssessmentTabs current="papers" />
      <PaperBuilder detail={detail} banks={banks} languages={languages} frameworks={frameworks} />
    </div>
  );
}
