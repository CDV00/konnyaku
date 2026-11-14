import { getFormatter, getLocale, getTranslations } from "next-intl/server";
import { Package as PackageIcon, Receipt } from "lucide-react";
import { requireMe } from "@konnyaku/session";
import { Input } from "@konnyaku/ui/components/ui/input";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@konnyaku/ui/components/ui/table";
import { EmptyState, PageHeader, Panel, StatusBadge } from "@konnyaku/ui/kk/blocks";
import { Field, NativeSelect } from "@konnyaku/ui/kk/field";
import { ActionForm, FormSheet } from "../action-form";
import { getJson, localized, statusTone, type ClassRow, type Package, type Student } from "../lib";

type Order = { id: string; studentId: string; packageId: string; classId: string | null; amount: number; amountReceived: number; currency: string; status: string };

const paymentMethods = ["cash", "bank_transfer", "card"] as const;

export default async function BillingPage() {
  const { token } = await requireMe("staff");
  const t = await getTranslations("admin");
  const locale = await getLocale();
  const format = await getFormatter();
  const [packages, orders, students, classes] = await Promise.all([
    getJson<Package[]>("/api/billing/packages", token, []),
    getJson<Order[]>("/api/billing/orders", token, []),
    getJson<Student[]>("/api/crm/students", token, []),
    getJson<ClassRow[]>("/api/academic/classes", token, []),
  ]);
  const money = (amount: number, currency: string) => format.number(amount, { style: "currency", currency });
  const studentName = new Map(students.map((s) => [s.id, s.fullName]));
  const packageName = new Map(packages.map((p) => [p.id, localized(p.name, locale)]));
  const className = new Map(classes.map((c) => [c.id, c.code]));

  return (
    <div className="space-y-6">
      <PageHeader
        title={t("billing.title")}
        description={t("billing.description")}
        actions={
          <>
            <FormSheet triggerLabel={t("billing.createPackage")} title={t("billing.createPackage")} variant="outline">
              <ActionForm api="billing/packages" submitLabel={t("save")}>
                <Field label={t("fields.code")}><Input name="code" required /></Field>
                <Field label={t("fields.nameVi")}><Input name="name.vi" required /></Field>
                <Field label={t("fields.nameEn")}><Input name="name.en" /></Field>
                <div className="grid grid-cols-2 gap-3">
                  <Field label={t("billing.paidCredits")}><Input name="paidCredits" type="number" min={1} required /></Field>
                  <Field label={t("billing.bonusCredits")}><Input name="bonusCredits" type="number" min={0} defaultValue={0} /></Field>
                </div>
                <div className="grid grid-cols-[1fr_6rem] gap-3">
                  <Field label={t("billing.price")}><Input name="price" type="number" min={0} step={1000} required /></Field>
                  <Field label={t("fields.currency")}><Input name="currency" required defaultValue="VND" maxLength={3} /></Field>
                </div>
              </ActionForm>
            </FormSheet>
            <FormSheet triggerLabel={t("billing.createOrder")} title={t("billing.createOrder")} description={t("billing.orderHint")}>
              <ActionForm api="billing/orders" submitLabel={t("save")}>
                <Field label={t("billing.student")}>
                  <NativeSelect name="studentId" required>
                    {students.map((s) => <option key={s.id} value={s.id}>{s.fullName}</option>)}
                  </NativeSelect>
                </Field>
                <Field label={t("billing.package")}>
                  <NativeSelect name="packageId" required>
                    {packages.map((p) => (
                      <option key={p.id} value={p.id}>
                        {t("billing.packageOption", { name: `${p.code} · ${packageName.get(p.id)}`, price: money(p.price, p.currency) })}
                      </option>
                    ))}
                  </NativeSelect>
                </Field>
                <Field label={t("billing.class")}>
                  <NativeSelect name="classId">
                    <option value="">{t("billing.noClass")}</option>
                    {classes.filter((c) => c.status === "Open").map((c) => <option key={c.id} value={c.id}>{c.code}</option>)}
                  </NativeSelect>
                </Field>
              </ActionForm>
            </FormSheet>
          </>
        }
      />

      <Panel title={t("billing.orders")} bodyClassName="p-0">
        {orders.length === 0 ? (
          <div className="p-5"><EmptyState icon={Receipt} title={t("billing.noOrders")} /></div>
        ) : (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead className="pl-5">{t("billing.student")}</TableHead>
                <TableHead>{t("billing.package")}</TableHead>
                <TableHead className="text-right">{t("billing.amount")}</TableHead>
                <TableHead className="text-right">{t("billing.received")}</TableHead>
                <TableHead>{t("fields.status")}</TableHead>
                <TableHead className="pr-5 text-right" />
              </TableRow>
            </TableHeader>
            <TableBody>
              {orders.map((o) => (
                <TableRow key={o.id}>
                  <TableCell className="pl-5">
                    <p className="font-medium">{studentName.get(o.studentId) ?? o.studentId}</p>
                    {o.classId && <p className="text-xs text-muted-foreground">{t("billing.forClass", { code: className.get(o.classId) ?? "" })}</p>}
                  </TableCell>
                  <TableCell>{packageName.get(o.packageId)}</TableCell>
                  <TableCell className="text-right tabular-nums">{money(o.amount, o.currency)}</TableCell>
                  <TableCell className="text-right tabular-nums">{money(o.amountReceived, o.currency)}</TableCell>
                  <TableCell><StatusBadge tone={statusTone[o.status]}>{t(`orderStatus.${o.status}`)}</StatusBadge></TableCell>
                  <TableCell className="pr-5 text-right">
                    {o.status === "PendingPayment" && (
                      <FormSheet
                        triggerLabel={t("billing.recordPayment")}
                        title={t("billing.recordPayment")}
                        description={t("billing.paymentFor", { name: studentName.get(o.studentId) ?? "", amount: money(o.amount - o.amountReceived, o.currency) })}
                        size="sm"
                        icon={false}
                      >
                        <ActionForm api={`billing/orders/${o.id}/payments`} submitLabel={t("billing.recordPayment")}>
                          <Field label={t("billing.amount")}>
                            <Input name="amount" type="number" min={1} required defaultValue={o.amount - o.amountReceived} />
                          </Field>
                          <Field label={t("billing.method")}>
                            <NativeSelect name="method">
                              {paymentMethods.map((m) => <option key={m} value={m}>{t(`billing.methods.${m}`)}</option>)}
                            </NativeSelect>
                          </Field>
                          <Field label={t("billing.reference")}><Input name="reference" /></Field>
                        </ActionForm>
                      </FormSheet>
                    )}
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </Panel>

      <div className="space-y-3">
        <h2 className="font-semibold tracking-tight">{t("billing.packages")}</h2>
        {packages.length === 0 ? (
          <EmptyState icon={PackageIcon} title={t("billing.noPackages")} />
        ) : (
          <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
            {packages.map((p) => (
              <article key={p.id} className="rounded-xl border bg-card p-5 shadow-xs">
                <div className="flex items-center justify-between">
                  <span className="rounded bg-muted px-2 py-0.5 font-mono text-xs">{p.code}</span>
                  {p.bonusCredits > 0 && <StatusBadge tone="highlight">{t("billing.bonusTag", { bonus: p.bonusCredits })}</StatusBadge>}
                </div>
                <h3 className="mt-3 font-semibold">{localized(p.name, locale)}</h3>
                <p className="mt-1 text-sm text-muted-foreground">{t("billing.creditsValue", { paid: p.paidCredits, bonus: p.bonusCredits })}</p>
                <p className="mt-4 text-2xl font-semibold tracking-tight tabular-nums">{money(p.price, p.currency)}</p>
              </article>
            ))}
          </div>
        )}
      </div>
    </div>
  );
}
