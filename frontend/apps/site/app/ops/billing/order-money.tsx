"use client";

import { useEffect, useState } from "react";
import Link from "next/link";
import { useFormatter, useTranslations } from "next-intl";
import { FileText } from "lucide-react";
import { Input } from "@konnyaku/ui/components/ui/input";
import { Textarea } from "@konnyaku/ui/components/ui/textarea";
import { StatusBadge } from "@konnyaku/ui/kk/blocks";
import { Field, NativeSelect } from "@konnyaku/ui/kk/field";
import { ActionForm } from "../action-form";

/** Loads a JSON read through the ops relay when the drawer opens (null while loading, fallback on error). */
function useRelay<T>(path: string, fallback: T): T | null {
  const [data, setData] = useState<T | null>(null);
  useEffect(() => {
    let cancelled = false;
    fetch(`/ops/api/relay/${path}`)
      .then((r) => (r.ok ? (r.json() as Promise<T>) : fallback))
      .then((value) => {
        if (!cancelled) setData(value);
      })
      .catch(() => {
        if (!cancelled) setData(fallback);
      });
    return () => {
      cancelled = true;
    };
    // The fallback is a constant per call site.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [path]);
  return data;
}

type Quote = {
  refundable: boolean;
  kind: string | null;
  amountReceived: number;
  paidCreditsLeft: number;
  bonusCreditsLeft: number;
  unitAmount: number;
  unusedValue: number;
  suggestedAmount: number;
  currency: string;
};

const methods = ["cash", "bank_transfer", "card"] as const;

/** Refund drawer body: the rule's breakdown, then the amount (suggested, editable), method, reference and reason. */
export function RefundForm({ orderId }: { orderId: string }) {
  const t = useTranslations("admin");
  const format = useFormatter();
  const quote = useRelay<Quote | null>(`billing/orders/${orderId}/refund-quote`, null);

  if (quote === null) return <p className="pt-4 text-sm text-muted-foreground">{t("history.loading")}</p>;
  if (!quote?.refundable) return <p className="pt-4 text-sm text-muted-foreground">{t("billing.quoteNotRefundable")}</p>;

  const money = (amount: number) => format.number(amount, { style: "currency", currency: quote.currency });
  const rows: [string, string][] = [
    [t("billing.quoteReceived"), money(quote.amountReceived)],
    [t("billing.quotePaidLeft"), format.number(quote.paidCreditsLeft)],
    [t("billing.quoteBonusLeft"), format.number(quote.bonusCreditsLeft)],
    [t("billing.quoteUnit"), money(quote.unitAmount)],
  ];
  return (
    <div className="grid gap-2 pt-4">
      {quote.kind && <StatusBadge tone="warning">{t(`billing.refundKinds.${quote.kind}`)}</StatusBadge>}
      <dl className="grid gap-1.5 rounded-lg border p-3 text-sm">
        {rows.map(([label, value]) => (
          <div key={label} className="flex justify-between gap-3">
            <dt className="text-muted-foreground">{label}</dt>
            <dd className="tabular-nums">{value}</dd>
          </div>
        ))}
        <div className="flex justify-between gap-3 border-t pt-1.5 font-semibold">
          <dt>{t("billing.quoteSuggested")}</dt>
          <dd className="tabular-nums">{money(quote.suggestedAmount)}</dd>
        </div>
      </dl>
      <ActionForm api={`billing/orders/${orderId}/refunds`} submitLabel={t("billing.refundSubmit")}>
        <Field label={t("billing.refundAmount")} hint={t("billing.refundAmountHint", { max: money(quote.amountReceived) })}>
          <Input name="amount" type="number" min={1} max={quote.amountReceived} step="any" required defaultValue={quote.suggestedAmount} />
        </Field>
        <Field label={t("billing.method")}>
          <NativeSelect name="method" defaultValue="bank_transfer">
            {methods.map((m) => <option key={m} value={m}>{t(`billing.methods.${m}`)}</option>)}
          </NativeSelect>
        </Field>
        <Field label={t("billing.reference")}><Input name="reference" maxLength={100} /></Field>
        <Field label={t("billing.refundReason")} hint={t("billing.refundReasonHint")}>
          <Textarea name="reason" required maxLength={300} rows={3} />
        </Field>
      </ActionForm>
    </div>
  );
}

type Refund = { id: string; receiptCode: string; amount: number; currency: string; reason: string; approvedByName: string | null; refundedAt: string };

export function RefundList({ orderId }: { orderId: string }) {
  const t = useTranslations("admin");
  const format = useFormatter();
  const refunds = useRelay<Refund[]>(`billing/orders/${orderId}/refunds`, []);
  if (!refunds || refunds.length === 0) return null;
  return (
    <section className="mt-6 space-y-3">
      <h3 className="text-sm font-semibold">{t("billing.refunds")}</h3>
      <ul className="space-y-2">
        {refunds.map((r) => (
          <li key={r.id} className="grid gap-1 rounded-lg border p-3 text-sm">
            <div className="flex flex-wrap items-center justify-between gap-2">
              <span className="font-medium tabular-nums">
                {t("billing.refundLine", { code: r.receiptCode, amount: format.number(r.amount, { style: "currency", currency: r.currency }), who: r.approvedByName ?? "—" })}
              </span>
              <ReceiptLink kind="refund" id={r.id} />
            </div>
            <span className="text-muted-foreground">{r.reason}</span>
            <span className="text-xs text-muted-foreground">{format.dateTime(new Date(r.refundedAt), { dateStyle: "short", timeStyle: "short" })}</span>
          </li>
        ))}
      </ul>
    </section>
  );
}

export function ReceiptLink({ kind, id }: { kind: "receipt" | "refund"; id: string }) {
  const t = useTranslations("admin");
  return (
    <Link href={`/ops/billing/receipts/${kind}/${id}`} className="inline-flex items-center gap-1 text-xs font-medium text-primary hover:underline">
      <FileText className="size-3.5" aria-hidden="true" />
      {t("billing.viewReceipt")}
    </Link>
  );
}

type LedgerLine = {
  id: string;
  entryType: string;
  creditKind: string;
  credits: number;
  unitAmount: number;
  currency: string;
  sessionLocalDate: string | null;
  reason: string | null;
  createdAt: string;
};

/** The order's lesson ledger (append-only lines) and, for accountants, a form adding an adjustment line. */
export function Ledger({ orderId, canAdjust }: { orderId: string; canAdjust: boolean }) {
  const t = useTranslations("admin");
  const format = useFormatter();
  const lines = useRelay<LedgerLine[]>(`billing/accounts/${orderId}/ledger`, []);
  if (!lines) return <p className="text-sm text-muted-foreground">{t("history.loading")}</p>;
  if (lines.length === 0) return <p className="text-sm text-muted-foreground">{t("billing.ledgerEmpty")}</p>;
  return (
    <div className="space-y-3">
      <p className="text-xs text-muted-foreground">{t("billing.ledgerHint")}</p>
      <ol className="divide-y rounded-lg border text-sm">
        {lines.map((l) => (
          <li key={l.id} className="grid gap-0.5 px-3 py-2">
            <div className="flex flex-wrap items-center justify-between gap-2">
              <span className="font-medium">{t(`billing.entryTypes.${l.entryType}`)}</span>
              <span className={`tabular-nums ${l.credits < 0 ? "text-destructive" : "text-success"}`}>
                {t("billing.ledgerCredits", {
                  credits: `${l.credits > 0 ? "+" : ""}${format.number(l.credits)}`,
                  kind: t(`billing.creditKinds.${l.creditKind}`),
                })}
              </span>
            </div>
            <span className="text-xs text-muted-foreground">
              {format.dateTime(new Date(l.sessionLocalDate ?? l.createdAt), { dateStyle: "short" })}
              {l.reason ? ` · ${l.reason}` : ""}
            </span>
          </li>
        ))}
      </ol>
      {canAdjust && (
        <details className="rounded-lg border p-3">
          <summary className="cursor-pointer text-sm font-medium">{t("billing.adjust")}</summary>
          <ActionForm api={`billing/accounts/${orderId}/adjustments`} submitLabel={t("billing.adjustSubmit")}>
            <div className="grid grid-cols-2 gap-3">
              <Field label={t("billing.adjustCredits")}><Input name="credits" type="number" step="0.5" required /></Field>
              <Field label={t("billing.adjustKind")}>
                <NativeSelect name="kind" defaultValue="Bonus">
                  <option value="Bonus">{t("billing.creditKinds.Bonus")}</option>
                  <option value="Paid">{t("billing.creditKinds.Paid")}</option>
                </NativeSelect>
              </Field>
            </div>
            <Field label={t("billing.adjustReason")}><Input name="reason" required maxLength={300} /></Field>
          </ActionForm>
        </details>
      )}
    </div>
  );
}
