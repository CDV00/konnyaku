import Link from "next/link";
import { notFound } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { ArrowLeft } from "lucide-react";
import { apiFetch, requireMe } from "@konnyaku/session";
import { PageHeader, StatusBadge } from "@konnyaku/ui/kk/blocks";
import type { BankDetail, Framework } from "@/assessment/types";
import { AssessmentTabs } from "../../assessment-tabs";
import { localizedName, type Language } from "../../authoring";
import { BankFormSheet } from "../bank-form";
import { BankEditor } from "./bank-editor";

export default async function BankPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  if (!/^[0-9a-f-]{36}$/i.test(id)) notFound();
  const { token } = await requireMe("staff");
  const [bankResponse, languagesResponse] = await Promise.all([
    apiFetch(`/api/assessment/banks/${id}?archived=true`, { token }),
    apiFetch("/api/catalog/languages", { token }),
  ]);
  if (!bankResponse.ok) notFound();
  const detail = (await bankResponse.json()) as BankDetail;
  const languages = languagesResponse.ok ? ((await languagesResponse.json()) as Language[]) : [];
  const frameworksResponse = await apiFetch(`/api/catalog/frameworks?language=${encodeURIComponent(detail.bank.languageCode)}`, { token });
  const frameworks = frameworksResponse.ok ? ((await frameworksResponse.json()) as Framework[]) : [];
  const t = await getTranslations("assessmentAuthoring");
  const locale = await getLocale();
  const language = localizedName(languages.find((l) => l.code === detail.bank.languageCode)?.name, locale) || detail.bank.languageCode;

  return (
    <div className="space-y-6">
      <Link href="/teach/assessments/banks" className="inline-flex items-center gap-1 text-sm text-muted-foreground hover:text-foreground">
        <ArrowLeft className="size-4" />
        {t("banks.back")}
      </Link>
      <PageHeader
        title={detail.bank.name}
        description={
          <span className="flex flex-wrap items-center gap-2">
            <span>{language}</span>
            <StatusBadge tone={detail.bank.visibility === "Shared" ? "primary" : "muted"}>{t(`banks.visibilities.${detail.bank.visibility}`)}</StatusBadge>
            {!detail.bank.canEdit && <StatusBadge tone="warning">{t("banks.readOnly")}</StatusBadge>}
          </span>
        }
        actions={detail.bank.canEdit && <BankFormSheet languages={languages} bank={detail.bank} />}
      />
      {detail.bank.description && <p className="text-sm text-muted-foreground">{detail.bank.description}</p>}
      <AssessmentTabs current="banks" />
      <BankEditor detail={detail} frameworks={frameworks} />
    </div>
  );
}
