import { getFormatter, getTranslations } from "next-intl/server";
import { Lock, Wallet } from "lucide-react";
import { requireMe } from "@konnyaku/session";
import { Input } from "@konnyaku/ui/components/ui/input";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@konnyaku/ui/components/ui/table";
import { EmptyState, PageHeader, Panel, StatusBadge } from "@konnyaku/ui/kk/blocks";
import { Field } from "@konnyaku/ui/kk/field";
import { ActionButton, ActionForm, FormSheet } from "../action-form";
import { getJson } from "../lib";

type Payslip = { teacherId: string; teacherName: string; currency: string; sessions: number; paidMinutes: number; amount: number; unpricedSessions: number };
type Period = { id: string; from: string; to: string; status: "Open" | "Locked"; payslips: Payslip[] };

export default async function PayrollPage() {
  const { token } = await requireMe("staff");
  const t = await getTranslations("payroll");
  const format = await getFormatter();
  const periods = await getJson<Period[]>("/api/hr/payroll-periods", token, []);
  return (
    <div className="space-y-6">
      <PageHeader
        title={t("title")}
        description={t("description")}
        actions={
          <FormSheet triggerLabel={t("create")} title={t("create")} description={t("createHint")}>
            <ActionForm endpoint="/ops/api/payroll" submitLabel={t("create")}>
              <div className="grid grid-cols-2 gap-3">
                <Field label={t("from")}><Input name="from" type="date" required /></Field>
                <Field label={t("to")}><Input name="to" type="date" required /></Field>
              </div>
            </ActionForm>
          </FormSheet>
        }
      />
      {periods.length === 0 && <EmptyState icon={Wallet} title={t("empty")} />}
      {periods.map((p) => {
        const totals = new Map<string, number>();
        for (const s of p.payslips) if (s.currency) totals.set(s.currency, (totals.get(s.currency) ?? 0) + s.amount);
        return (
          <Panel
            key={p.id}
            title={
              <span className="flex flex-wrap items-center gap-3">
                {t("range", { from: p.from, to: p.to })}
                <StatusBadge tone={p.status === "Locked" ? "success" : "warning"}>
                  {p.status === "Locked" && <Lock className="size-3" />}
                  {t(`status.${p.status}`)}
                </StatusBadge>
              </span>
            }
            description={[...totals].map(([currency, amount]) => t("total", { amount: format.number(amount, { style: "currency", currency }) })).join(" · ")}
            action={p.status === "Open" && <ActionButton endpoint={`/ops/api/payroll/${p.id}/lock`} label={t("lock")} variant="default" />}
            bodyClassName="p-0"
          >
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead className="pl-5">{t("teacher")}</TableHead>
                  <TableHead className="text-right">{t("sessions")}</TableHead>
                  <TableHead className="text-right">{t("minutes")}</TableHead>
                  <TableHead className="text-right">{t("amount")}</TableHead>
                  <TableHead className="pr-5 text-right">{t("unpriced")}</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {p.payslips.map((s) => (
                  <TableRow key={`${s.teacherId}-${s.currency}`}>
                    <TableCell className="pl-5 font-medium">{s.teacherName}</TableCell>
                    <TableCell className="text-right tabular-nums">{s.sessions}</TableCell>
                    <TableCell className="text-right tabular-nums">{s.paidMinutes}</TableCell>
                    <TableCell className="text-right font-medium tabular-nums">
                      {s.currency ? format.number(s.amount, { style: "currency", currency: s.currency }) : null}
                    </TableCell>
                    <TableCell className="pr-5 text-right">
                      {s.unpricedSessions > 0 ? <StatusBadge tone="warning">{s.unpricedSessions}</StatusBadge> : <span className="text-muted-foreground">{0}</span>}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </Panel>
        );
      })}
    </div>
  );
}
