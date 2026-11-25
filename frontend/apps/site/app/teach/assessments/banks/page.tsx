import Link from "next/link";
import { getLocale, getTranslations } from "next-intl/server";
import { Library } from "lucide-react";
import { apiFetch, requireMe } from "@konnyaku/session";
import { EmptyState, PageHeader, Panel, StatusBadge } from "@konnyaku/ui/kk/blocks";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@konnyaku/ui/components/ui/table";
import type { Bank } from "@/assessment/types";
import { AssessmentTabs } from "../assessment-tabs";
import { localizedName, type Language } from "../authoring";
import { BankFormSheet } from "./bank-form";

export default async function BanksPage() {
  const { token } = await requireMe("staff");
  const [banksResponse, languagesResponse] = await Promise.all([
    apiFetch("/api/assessment/banks", { token }),
    apiFetch("/api/catalog/languages", { token }),
  ]);
  const banks = banksResponse.ok ? ((await banksResponse.json()) as Bank[]) : [];
  const languages = languagesResponse.ok ? ((await languagesResponse.json()) as Language[]) : [];
  const t = await getTranslations("assessmentAuthoring");
  const locale = await getLocale();
  const languageName = (code: string) => localizedName(languages.find((l) => l.code === code)?.name, locale) || code;

  return (
    <div className="space-y-6">
      <PageHeader title={t("banks.title")} description={t("banks.subtitle")} actions={<BankFormSheet languages={languages} />} />
      <AssessmentTabs current="banks" />
      <Panel bodyClassName="p-0">
        {banks.length === 0 ? (
          <div className="p-5">
            <EmptyState icon={Library} title={t("banks.empty")} description={t("banks.emptyHint")} />
          </div>
        ) : (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>{t("banks.name")}</TableHead>
                <TableHead className="hidden sm:table-cell">{t("common.language")}</TableHead>
                <TableHead className="text-right">{t("banks.questions")}</TableHead>
                <TableHead className="hidden md:table-cell">{t("banks.visibility")}</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {banks.map((bank) => (
                <TableRow key={bank.id}>
                  <TableCell className="max-w-0 whitespace-normal sm:max-w-none">
                    <Link href={`/teach/assessments/banks/${bank.id}`} className="font-medium hover:underline">
                      {bank.name}
                    </Link>
                    {bank.description && <p className="line-clamp-1 text-xs text-muted-foreground">{bank.description}</p>}
                    <p className="text-xs text-muted-foreground sm:hidden">{languageName(bank.languageCode)}</p>
                  </TableCell>
                  <TableCell className="hidden sm:table-cell">{languageName(bank.languageCode)}</TableCell>
                  <TableCell className="text-right tabular-nums">{bank.questionCount}</TableCell>
                  <TableCell className="hidden md:table-cell">
                    <StatusBadge tone={bank.visibility === "Shared" ? "primary" : "muted"}>{t(`banks.visibilities.${bank.visibility}`)}</StatusBadge>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </Panel>
    </div>
  );
}
