import { getFormatter, getLocale, getTranslations } from "next-intl/server";
import { Download, Receipt } from "lucide-react";
import { apiFetch, requireMe } from "@konnyaku/session";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@konnyaku/ui/components/ui/table";
import { EmptyState, FormMessage, PageHeader, Panel, StatusBadge } from "@konnyaku/ui/kk/blocks";

type MyPayment = {
  kind: "receipt" | "refund";
  id: string;
  code: string;
  at: string;
  amount: number;
  currency: string;
  method: string;
  voided: boolean;
  studentName: string;
  packageCode: string;
  packageName: Record<string, string>;
};

/** The learner's (or parent's) payments and refunds, each with its receipt to download. */
export default async function PaymentsPage({ searchParams }: { searchParams: Promise<{ error?: string }> }) {
  const { token } = await requireMe("learn");
  const response = await apiFetch("/api/billing/my/payments", { token });
  const payments = response.ok ? ((await response.json()) as MyPayment[]) : [];
  const failed = (await searchParams).error === "download";
  const t = await getTranslations("learn.payments");
  const errors = await getTranslations("admin.errors");
  const locale = await getLocale();
  const format = await getFormatter();
  const showStudent = new Set(payments.map((p) => p.studentName)).size > 1;

  return (
    <div className="space-y-6">
      <PageHeader title={t("title")} description={t("description")} />
      {failed && <FormMessage>{errors("billing_receipt_not_found")}</FormMessage>}
      <Panel title={t("title")} bodyClassName="p-0">
        {payments.length === 0 ? (
          <div className="p-5"><EmptyState icon={Receipt} title={t("empty")} /></div>
        ) : (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead className="pl-5">{t("code")}</TableHead>
                <TableHead className="hidden sm:table-cell">{t("date")}</TableHead>
                <TableHead className="hidden md:table-cell">{t("package")}</TableHead>
                <TableHead className="text-right">{t("amount")}</TableHead>
                <TableHead className="pr-5 text-right" />
              </TableRow>
            </TableHeader>
            <TableBody>
              {payments.map((p) => (
                <TableRow key={p.id}>
                  <TableCell className="pl-5">
                    <p className="font-mono text-sm">{p.code}</p>
                    <p className="text-xs text-muted-foreground">
                      {p.kind === "refund" ? t("refund") : t("receipt")}
                      {showStudent ? ` · ${p.studentName}` : ""}
                    </p>
                    {p.voided && <StatusBadge tone="destructive">{t("voided")}</StatusBadge>}
                  </TableCell>
                  <TableCell className="hidden sm:table-cell">{format.dateTime(new Date(p.at), { dateStyle: "medium" })}</TableCell>
                  <TableCell className="hidden md:table-cell">{p.packageName[locale] ?? p.packageName.vi ?? p.packageCode}</TableCell>
                  <TableCell className={`text-right tabular-nums ${p.kind === "refund" ? "text-destructive" : ""}`}>
                    {p.kind === "refund" ? "−" : ""}
                    {format.number(p.amount, { style: "currency", currency: p.currency })}
                  </TableCell>
                  <TableCell className="pr-5 text-right">
                    <a
                      href={`/learn/receipts/${p.kind}/${p.id}`}
                      className="inline-flex items-center gap-1.5 rounded-md border px-3 py-1.5 text-xs font-medium hover:bg-muted"
                    >
                      <Download className="size-3.5" aria-hidden="true" />
                      {t("download")}
                    </a>
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
