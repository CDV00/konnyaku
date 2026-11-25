import Link from "next/link";
import { getLocale, getTranslations } from "next-intl/server";
import { FileText, Plus, Send } from "lucide-react";
import { apiFetch, requireMe } from "@konnyaku/session";
import { EmptyState, PageHeader, Panel, StatusBadge } from "@konnyaku/ui/kk/blocks";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@konnyaku/ui/components/ui/table";
import { LinkButton } from "@/assessment/link-button";
import type { Paper } from "@/assessment/types";
import { AssessmentTabs } from "../assessment-tabs";
import { localizedName, type Language } from "../authoring";

export default async function PapersPage() {
  const { token } = await requireMe("staff");
  const [papersResponse, languagesResponse] = await Promise.all([
    apiFetch("/api/assessment/papers?archived=true", { token }),
    apiFetch("/api/catalog/languages", { token }),
  ]);
  const papers = papersResponse.ok ? ((await papersResponse.json()) as Paper[]) : [];
  const languages = languagesResponse.ok ? ((await languagesResponse.json()) as Language[]) : [];
  const t = await getTranslations("assessmentAuthoring");
  const locale = await getLocale();
  const languageName = (code: string) => localizedName(languages.find((l) => l.code === code)?.name, locale) || code;
  // Active papers first, archived ones at the end.
  const sorted = [...papers].sort((a, b) => Number(a.archived) - Number(b.archived));

  const newPaper = (
    <LinkButton href="/teach/assessments/papers/new">
      <Plus />
      {t("papers.new")}
    </LinkButton>
  );

  return (
    <div className="space-y-6">
      <PageHeader title={t("papers.title")} description={t("papers.subtitle")} actions={newPaper} />
      <AssessmentTabs current="papers" />
      <Panel bodyClassName="p-0">
        {sorted.length === 0 ? (
          <div className="p-5">
            <EmptyState icon={FileText} title={t("papers.empty")} description={t("papers.emptyHint")} />
          </div>
        ) : (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>{t("papers.paperTitle")}</TableHead>
                <TableHead className="hidden sm:table-cell">{t("common.language")}</TableHead>
                <TableHead className="text-right">{t("papers.questions")}</TableHead>
                <TableHead className="hidden text-right md:table-cell">{t("papers.minutes")}</TableHead>
                <TableHead className="text-right">
                  <span className="sr-only">{t("papers.assign")}</span>
                </TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {sorted.map((paper) => (
                <TableRow key={paper.id} className={paper.archived ? "opacity-70" : undefined}>
                  <TableCell className="max-w-0 whitespace-normal sm:max-w-none">
                    <div className="flex flex-wrap items-center gap-2">
                      <Link href={`/teach/assessments/papers/${paper.id}`} className="font-medium hover:underline">
                        {paper.title}
                      </Link>
                      {paper.archived && <StatusBadge tone="warning">{t("papers.archived")}</StatusBadge>}
                    </div>
                    <p className="text-xs text-muted-foreground sm:hidden">{languageName(paper.languageCode)}</p>
                  </TableCell>
                  <TableCell className="hidden sm:table-cell">{languageName(paper.languageCode)}</TableCell>
                  <TableCell className="text-right tabular-nums">{paper.questionCount}</TableCell>
                  <TableCell className="hidden text-right tabular-nums md:table-cell">
                    {paper.totalMinutes === null ? t("papers.untimed") : t("papers.minutesValue", { minutes: paper.totalMinutes })}
                  </TableCell>
                  <TableCell className="text-right">
                    {!paper.archived && (
                      <LinkButton href={`/teach/assessments/assign?paper=${paper.id}`} variant="ghost" size="sm">
                        <Send />
                        <span className="hidden sm:inline">{t("papers.assign")}</span>
                      </LinkButton>
                    )}
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
