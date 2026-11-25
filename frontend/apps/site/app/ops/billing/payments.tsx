"use client";

import { useEffect, useState } from "react";
import { useFormatter, useTranslations } from "next-intl";
import { Input } from "@konnyaku/ui/components/ui/input";
import { StatusBadge } from "@konnyaku/ui/kk/blocks";
import { NativeSelect } from "@konnyaku/ui/kk/field";
import { ActionButton, ActionForm } from "../action-form";
import { ReceiptLink } from "./order-money";

type Payment = {
  id: string;
  amount: number;
  method: string;
  reference: string | null;
  receivedAt: string;
  voidedAt: string | null;
  receiptCode: string;
  receivedByName: string | null;
};

const methods = ["cash", "bank_transfer", "card"] as const;

/**
 * Payments of one order, loaded when the drawer opens. While the order waits for payment, a wrong entry can be
 * corrected or voided; after it is paid only the method and reference can be fixed (the ledger holds the credits).
 */
export function PaymentList({ orderId, currency, editable, pending }: { orderId: string; currency: string; editable: boolean; pending: boolean }) {
  const t = useTranslations("admin");
  const format = useFormatter();
  const [payments, setPayments] = useState<Payment[] | null>(null);

  useEffect(() => {
    let cancelled = false;
    fetch(`/ops/api/relay/billing/orders/${orderId}/payments`)
      .then((r) => (r.ok ? (r.json() as Promise<Payment[]>) : []))
      .then((list) => {
        if (!cancelled) setPayments(list);
      })
      .catch(() => {
        if (!cancelled) setPayments([]);
      });
    return () => {
      cancelled = true;
    };
  }, [orderId]);

  if (!payments) return <p className="text-sm text-muted-foreground">{t("history.loading")}</p>;
  if (payments.length === 0) return <p className="text-sm text-muted-foreground">{t("billing.noPayments")}</p>;

  return (
    <ul className="space-y-2">
      {payments.map((p) => (
        <li key={p.id} className="grid gap-2 rounded-lg border p-3 text-sm">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <span className="font-medium tabular-nums">
              {p.receiptCode} · {format.number(p.amount, { style: "currency", currency })}
            </span>
            <span className="text-xs text-muted-foreground">{format.dateTime(new Date(p.receivedAt), { dateStyle: "short", timeStyle: "short" })}</span>
          </div>
          <div className="flex flex-wrap items-center justify-between gap-2 text-xs text-muted-foreground">
            <span>{p.receivedByName ? t("billing.collectedBy", { name: p.receivedByName }) : null}</span>
            <ReceiptLink kind="receipt" id={p.id} />
          </div>
          {p.voidedAt ? (
            <StatusBadge tone="destructive">{t("billing.voided")}</StatusBadge>
          ) : editable ? (
            <>
              <ActionForm api={`billing/payments/${p.id}`} method="PUT" submitLabel={t("save")} layout="inline">
                <Input name="amount" type="number" min={1} required defaultValue={p.amount} readOnly={!pending} className="w-32" aria-label={t("billing.amount")} />
                <NativeSelect name="method" defaultValue={p.method} className="w-36" aria-label={t("billing.method")}>
                  {methods.map((m) => <option key={m} value={m}>{t(`billing.methods.${m}`)}</option>)}
                </NativeSelect>
                <Input name="reference" defaultValue={p.reference ?? ""} className="w-32" aria-label={t("billing.reference")} />
              </ActionForm>
              {pending && <ActionButton api={`billing/payments/${p.id}/void`} label={t("billing.void")} variant="ghost" confirm={t("billing.voidConfirm")} />}
            </>
          ) : (
            <span className="text-muted-foreground">{t(`billing.methods.${p.method}`)}{p.reference ? ` · ${p.reference}` : ""}</span>
          )}
        </li>
      ))}
    </ul>
  );
}
