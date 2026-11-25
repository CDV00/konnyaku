import Link from "next/link";
import { notFound } from "next/navigation";
import { getFormatter, getLocale, getTranslations } from "next-intl/server";
import { ArrowLeft, Download, Printer } from "lucide-react";
import { requireMe } from "@konnyaku/session";
import { PageHeader, Panel, StatusBadge } from "@konnyaku/ui/kk/blocks";
import { getJson, localized, type Localized } from "../../../../lib";

type Receipt = {
  kind: "receipt" | "refund";
  id: string;
  orderId: string;
  code: string;
  issuedAt: string;
  timeZone: string;
  voided: boolean;
  centerName: string;
  branchName: Localized | null;
  studentName: string;
  packageCode: string;
  packageName: Localized;
  classCode: string | null;
  amount: number;
  currency: string;
  amountInWords: string;
  method: string;
  reference: string | null;
  reason: string | null;
  issuedByName: string | null;
  paidCreditsReturned: number | null;
  bonusCreditsForfeited: number | null;
};

type Print = { id: string; action: "print" | "download"; audience: "staff" | "learn"; actorName: string | null; occurredAt: string };

const linkButton =
  "inline-flex h-9 items-center gap-2 rounded-md px-4 text-sm font-medium shadow-xs transition-colors focus-visible:ring-[3px] focus-visible:ring-ring/50 outline-none";

/** A receipt (phiếu thu) or refund voucher (phiếu chi): what will be printed, the print/download buttons and the print log. */
export default async function ReceiptPage({ params }: { params: Promise<{ kind: string; id: string }> }) {
  const { kind, id } = await params;
  if (kind !== "receipt" && kind !== "refund") notFound();
  const { token } = await requireMe("staff");
  const [receipt, prints] = await Promise.all([
    getJson<Receipt | null>(`/api/billing/receipts/${kind}/${id}`, token, null),
    getJson<Print[]>(`/api/billing/receipts/${kind}/${id}/prints`, token, []),
  ]);
  if (!receipt) notFound();
  const t = await getTranslations("admin");
  const locale = await getLocale();
  const format = await getFormatter();
  const money = format.number(receipt.amount, { style: "currency", currency: receipt.currency });
  const pkg = [receipt.packageCode, localized(receipt.packageName, locale)].filter(Boolean).join(" · ");
  const rows: [string, string | null][] = [
    [t("receipts.center"), receipt.centerName],
    [t("receipts.branch"), receipt.branchName ? localized(receipt.branchName, locale) : null],
    [t("receipts.date"), format.dateTime(new Date(receipt.issuedAt), { dateStyle: "medium", timeStyle: "short", timeZone: receipt.timeZone })],
    [t("receipts.student"), receipt.studentName],
    [t("receipts.package"), receipt.classCode ? `${pkg} · ${receipt.classCode}` : pkg],
    [t("receipts.method"), t(`billing.methods.${receipt.method}`)],
    [t("receipts.reference"), receipt.reference],
    [t("receipts.reason"), receipt.reason],
    [
      t("receipts.credits"),
      receipt.kind === "refund" ? t("receipts.creditsValue", { paid: receipt.paidCreditsReturned ?? 0, bonus: receipt.bonusCreditsForfeited ?? 0 }) : null,
    ],
    [t("receipts.issuedBy"), receipt.issuedByName],
  ];
  const pdf = `/ops/billing/receipts/${kind}/${id}/pdf`;

  return (
    <div className="space-y-6">
      <Link href="/ops/billing" className="inline-flex items-center gap-1.5 text-sm text-muted-foreground hover:text-foreground">
        <ArrowLeft className="size-4" aria-hidden="true" />
        {t("receipts.back")}
      </Link>
      <PageHeader
        title={t(receipt.kind === "refund" ? "receipts.refundTitle" : "receipts.receiptTitle", { code: receipt.code })}
        description={t("receipts.notice")}
        actions={
          <>
            <a href={pdf} target="_blank" rel="noopener" className={`${linkButton} border bg-background hover:bg-muted`}>
              <Printer className="size-4" aria-hidden="true" />
              {t("receipts.print")}
            </a>
            <a href={`${pdf}?download=1`} className={`${linkButton} bg-primary text-primary-foreground hover:bg-primary/90`}>
              <Download className="size-4" aria-hidden="true" />
              {t("receipts.download")}
            </a>
          </>
        }
      />
      {receipt.voided && <StatusBadge tone="destructive">{t("receipts.voided")}</StatusBadge>}

      <div className="grid gap-6 lg:grid-cols-[1.5fr_1fr]">
        <Panel title={receipt.code}>
          <div className="mb-4 rounded-lg bg-muted/50 p-4">
            <p className="text-xs text-muted-foreground">{t("receipts.amount")}</p>
            <p className="text-2xl font-semibold tracking-tight tabular-nums">{money}</p>
            <p className="mt-1 text-sm">
              <span className="text-muted-foreground">{t("receipts.inWords")}</span> <span className="italic">{receipt.amountInWords}</span>
            </p>
          </div>
          <dl className="grid gap-2 text-sm sm:grid-cols-[10rem_1fr]">
            {rows
              .filter((row): row is [string, string] => !!row[1])
              .map(([label, value]) => (
                <div key={label} className="contents">
                  <dt className="text-muted-foreground">{label}</dt>
                  <dd className="break-words">{value}</dd>
                </div>
              ))}
          </dl>
        </Panel>
        <Panel title={t("receipts.prints")}>
          {prints.length === 0 ? (
            <p className="text-sm text-muted-foreground">{t("receipts.noPrints")}</p>
          ) : (
            <ul className="space-y-2 text-sm">
              {prints.map((p) => (
                <li key={p.id}>
                  {t("receipts.printLine", {
                    who: `${p.actorName ?? "—"} (${t(`receipts.audiences.${p.audience}`)})`,
                    action: t(`receipts.actions.${p.action}`),
                    when: format.dateTime(new Date(p.occurredAt), { dateStyle: "short", timeStyle: "short" }),
                  })}
                </li>
              ))}
            </ul>
          )}
        </Panel>
      </div>
    </div>
  );
}
