
import { getFormatter, getLocale, getTranslations } from "next-intl/server";
import { Package as PackageIcon, Receipt, SearchX } from "lucide-react";
import { requireMe } from "@konnyaku/session";
import { Input } from "@konnyaku/ui/components/ui/input";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@konnyaku/ui/components/ui/table";
import { EmptyState, PageHeader, Panel, StatusBadge } from "@konnyaku/ui/kk/blocks";
import { Field, NativeSelect } from "@konnyaku/ui/kk/field";
import { ActionButton, ActionForm, FormSheet } from "../action-form";
import { orderFilters } from "../filters";
import { ChangeHistory } from "../history";
import { emptyPage, getAccess, getJson, listQuery, localized, namesOf, statusTone, type ClassRow, type Package, type Page, type SearchParams } from "../lib";
import { FilterDate, FilterSelect, ListBody, ListFrame, ListToolbar, Pagination, SortHeader } from "../list";
import { RecordPicker } from "../personal-data";

import { Ledger, RefundForm, RefundList } from "./order-money";
import { PaymentList } from "./payments";

type Order = {
  id: string;
  studentId: string;
  packageId: string;
  classId: string | null;
  amount: number;
  amountReceived: number;
  currency: string;
  status: string;
  createdAt: string;
  branchId: string | null;
  amountRefunded: number;
};

/** Status filters of the orders list, as the API names them (`/api/billing/orders?status=`). */
const statusFilters = ["unpaid", "partial", "paid", "refund_required", "refunded", "cancelled"] as const;

const paymentMethods = ["cash", "bank_transfer", "card"] as const;

export default async function BillingPage({ searchParams }: { searchParams: SearchParams }) {
  const { token } = await requireMe("staff");

  const t = await getTranslations("admin");
  const locale = await getLocale();
  const format = await getFormatter();
  const access = await getAccess(token);
  const canPackages = access.can("billing.packages");
  const canOrders = access.can("billing.orders");
  const canPayments = access.can("billing.payments");
  const canSell = canOrders || access.any("crm.students");
  const query = await listQuery(searchParams, orderFilters);
  const [packages, page, open] = await Promise.all([
    getJson<Package[]>(`/api/billing/packages${canPackages ? "?includeInactive=true" : ""}`, token, []),
    getJson<Page<Order>>(`/api/billing/orders?${query.toString()}`, token, emptyPage()),
    getJson<Page<ClassRow>>("/api/academic/classes?status=Open&pageSize=200", token, emptyPage()),
  ]);
  const orders = page.items;
  // Names of the students and classes on this page only.
  const classIds = [...new Set(orders.map((o) => o.classId).filter((id): id is string => !!id))];
  const [studentName, pageClasses] = await Promise.all([
    namesOf("/api/crm/students", orders.map((o) => o.studentId), token),
    classIds.length > 0
      ? getJson<Page<ClassRow>>(`/api/academic/classes?status=all&pageSize=200&ids=${classIds.join(",")}`, token, emptyPage())
      : Promise.resolve(emptyPage<ClassRow>()),
  ]);
  const classes = [...open.items, ...pageClasses.items.filter((c) => !open.items.some((o) => o.id === c.id))];
  const money = (amount: number, currency: string) => format.number(amount, { style: "currency", currency });
  const packageName = new Map(packages.map((p) => [p.id, localized(p.name, locale)]));
  const className = new Map(classes.map((c) => [c.id, c.code]));
  const openClasses = open.items;
  const names: Record<string, string> = Object.fromEntries([
    ...classes.map((c) => [c.id, c.code]),
    ...studentName.entries(),
    ...packages.map((p) => [p.id, p.code]),
  ]);
  const filtered = orderFilters.some((key) => query.has(key));
  const packageFields = (p?: Package) => (
    <>
      <Field label={t("fields.code")}><Input name="code" required defaultValue={p?.code} /></Field>
      <Field label={t("fields.nameVi")}><Input name="name.vi" required defaultValue={p?.name.vi} /></Field>
      <Field label={t("fields.nameEn")}><Input name="name.en" defaultValue={p?.name.en} /></Field>
      <div className="grid grid-cols-2 gap-3">
        <Field label={t("billing.paidCredits")}><Input name="paidCredits" type="number" min={1} required defaultValue={p?.paidCredits} /></Field>
        <Field label={t("billing.bonusCredits")}><Input name="bonusCredits" type="number" min={0} defaultValue={p?.bonusCredits ?? 0} /></Field>
      </div>
      <div className="grid grid-cols-[1fr_6rem] gap-3">
        <Field label={t("billing.price")}><Input name="price" type="number" min={0} step={1000} required defaultValue={p?.price} /></Field>
        <Field label={t("fields.currency")}><Input name="currency" required defaultValue={p?.currency ?? "VND"} maxLength={3} /></Field>
      </div>
    </>
  );
  const classSelect = (current?: string | null) => (
    <Field label={t("billing.class")}>
      <NativeSelect name="classId" defaultValue={current ?? ""}>
        <option value="">{t("billing.noClass")}</option>
        {classes.filter((c) => c.status === "Open" || c.id === current).map((c) => <option key={c.id} value={c.id}>{c.code}</option>)}
      </NativeSelect>
    </Field>
  );

  return (
    <div className="space-y-6">
      <PageHeader
        title={t("billing.title")}
        description={t("billing.description")}
        actions={
          <>
            {canPackages && (
              <FormSheet triggerLabel={t("billing.createPackage")} title={t("billing.createPackage")} variant="outline">
                <ActionForm api="billing/packages" submitLabel={t("save")}>{packageFields()}</ActionForm>
              </FormSheet>
            )}
            {canSell && (
              <FormSheet triggerLabel={t("billing.createOrder")} title={t("billing.createOrder")} description={t("billing.orderHint")}>
                <ActionForm api="billing/orders" submitLabel={t("save")}>
                  <Field label={t("billing.student")}>
                    <RecordPicker name="studentId" source="crm/students" filter="status=active" placeholder={t("billing.findStudent")} required />
                  </Field>
                  <Field label={t("billing.package")}>
                    <NativeSelect name="packageId" required>
                      {packages.filter((p) => p.isActive).map((p) => (
                        <option key={p.id} value={p.id}>
                          {t("billing.packageOption", { name: `${p.code} · ${packageName.get(p.id)}`, price: money(p.price, p.currency) })}
                        </option>
                      ))}
                    </NativeSelect>
                  </Field>
                  {openClasses.length > 0 && classSelect()}
                </ActionForm>
              </FormSheet>
            )}
          </>
        }
      />

      <ListFrame>
      <h2 className="font-semibold tracking-tight">{t("billing.orders")}</h2>
      <ListToolbar placeholder={t("billing.search")}>
        <FilterSelect name="status" label={t("fields.status")} allLabel={t("billing.filters.all")} options={statusFilters.map((s) => ({ value: s, label: t(`billing.filters.${s}`) }))} />
        <FilterSelect name="package" label={t("billing.package")} allLabel={t("list.allPackages")} options={packages.map((p) => ({ value: p.id, label: p.code }))} />
        <FilterDate name="createdFrom" label={t("list.from")} />
        <FilterDate name="createdTo" label={t("list.to")} />
      </ListToolbar>
      <ListBody>
      <Panel bodyClassName="p-0">
        {orders.length === 0 ? (
          <div className="p-5">{filtered ? <EmptyState icon={SearchX} title={t("list.noResults")} /> : <EmptyState icon={Receipt} title={t("billing.noOrders")} />}</div>
        ) : (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead className="pl-5">{t("billing.student")}</TableHead>
                <TableHead className="hidden md:table-cell">{t("billing.package")}</TableHead>
                <TableHead className="text-right"><SortHeader field="amount" label={t("billing.amount")} defaultField="createdAt" defaultDesc /></TableHead>
                <TableHead className="hidden text-right sm:table-cell">{t("billing.received")}</TableHead>
                <TableHead className="hidden lg:table-cell"><SortHeader field="createdAt" label={t("billing.createdAt")} defaultField="createdAt" defaultDesc /></TableHead>
                <TableHead><SortHeader field="status" label={t("fields.status")} defaultField="createdAt" defaultDesc /></TableHead>
                <TableHead className="pr-5 text-right" />
              </TableRow>
            </TableHeader>
            <TableBody>
              {orders.map((o) => {
                const pending = o.status === "PendingPayment";
                const refundable = o.status === "Paid" || o.status === "RefundRequired" || (pending && o.amountReceived > 0);
                const canRefund = refundable && access.can("billing.refunds", o.branchId);
                return (
                  <TableRow key={o.id}>
                    <TableCell className="pl-5">
                      <p className="font-medium">{studentName.get(o.studentId) ?? o.studentId}</p>
                      {o.classId && <p className="text-xs text-muted-foreground">{t("billing.forClass", { code: className.get(o.classId) ?? "" })}</p>}
                    </TableCell>
                    <TableCell className="hidden md:table-cell">{packageName.get(o.packageId)}</TableCell>
                    <TableCell className="text-right tabular-nums">{money(o.amount, o.currency)}</TableCell>
                    <TableCell className="hidden text-right tabular-nums sm:table-cell">{money(o.amountReceived, o.currency)}</TableCell>
                    <TableCell className="hidden text-muted-foreground tabular-nums lg:table-cell">{format.dateTime(new Date(o.createdAt), { dateStyle: "short" })}</TableCell>
                    <TableCell><StatusBadge tone={statusTone[o.status]}>{t(`orderStatus.${o.status}`)}</StatusBadge></TableCell>
                    <TableCell className="pr-5 text-right">
                      <span className="inline-flex flex-wrap items-start justify-end gap-2">
                        {pending && canPayments && (
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
                        {canRefund && (
                          <FormSheet
                            triggerLabel={t("billing.refund")}
                            title={t("billing.refundTitle", { name: studentName.get(o.studentId) ?? "" })}
                            description={t("billing.refundDescription")}
                            variant={o.status === "RefundRequired" ? "destructive" : "outline"}
                            size="sm"
                            icon={false}
                          >
                            <RefundForm orderId={o.id} />
                          </FormSheet>
                        )}
                        <FormSheet triggerLabel={t("billing.details")} title={t("billing.orderOf", { name: studentName.get(o.studentId) ?? "" })} variant="ghost" size="sm" icon={false}>
                          {pending && canOrders && (
                            <section className="space-y-3 pt-4">
                              <h3 className="text-sm font-semibold">{t("billing.editOrder")}</h3>
                              <ActionForm api={`billing/orders/${o.id}`} method="PUT" submitLabel={t("save")}>
                                <Field label={t("billing.amount")} hint={t("billing.amountHint", { received: money(o.amountReceived, o.currency) })}>
                                  <Input name="amount" type="number" min={0} step={1000} required defaultValue={o.amount} />
                                </Field>
                                {classSelect(o.classId)}
                              </ActionForm>
                              {o.amountReceived === 0 && (
                                <ActionButton api={`billing/orders/${o.id}/cancel`} label={t("billing.cancelOrder")} variant="destructive" confirm={t("billing.cancelOrderConfirm")} />
                              )}
                            </section>
                          )}
                          <section className="mt-6 space-y-3">
                            <h3 className="text-sm font-semibold">{t("billing.payments")}</h3>
                            <PaymentList orderId={o.id} currency={o.currency} editable={canPayments} pending={pending} />
                          </section>
                          <RefundList orderId={o.id} />
                          <section className="mt-6 space-y-3">
                            <h3 className="text-sm font-semibold">{t("billing.ledger")}</h3>
                            <Ledger orderId={o.id} canAdjust={o.status === "Paid" && access.can("billing.orders", o.branchId)} />
                          </section>
                          <section className="mt-6 space-y-3">
                            <h3 className="text-sm font-semibold">{t("history.title")}</h3>
                            <ChangeHistory sources={[{ module: "billing", id: o.id }]} names={names} />
                          </section>
                        </FormSheet>
                      </span>
                    </TableCell>
                  </TableRow>
                );
              })}
            </TableBody>
          </Table>
        )}
      </Panel>
      </ListBody>
      <Pagination total={page.total} page={page.pageNumber} pageSize={page.pageSize} />
      </ListFrame>

      <div className="space-y-3">
        <h2 className="font-semibold tracking-tight">{t("billing.packages")}</h2>
        {packages.length === 0 ? (
          <EmptyState icon={PackageIcon} title={t("billing.noPackages")} />
        ) : (
          <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
            {packages.map((p) => (
              <article key={p.id} className={`flex flex-col rounded-xl border bg-card p-5 shadow-xs ${p.isActive ? "" : "opacity-70"}`}>
                <div className="flex items-center justify-between gap-2">
                  <span className="rounded bg-muted px-2 py-0.5 font-mono text-xs">{p.code}</span>
                  <span className="flex gap-1.5">
                    {!p.isActive && <StatusBadge tone="muted">{t("inactive")}</StatusBadge>}
                    {p.bonusCredits > 0 && <StatusBadge tone="highlight">{t("billing.bonusTag", { bonus: p.bonusCredits })}</StatusBadge>}
                  </span>
                </div>
                <h3 className="mt-3 font-semibold">{localized(p.name, locale)}</h3>
                <p className="mt-1 text-sm text-muted-foreground">{t("billing.creditsValue", { paid: p.paidCredits, bonus: p.bonusCredits })}</p>
                <p className="mt-4 text-2xl font-semibold tracking-tight tabular-nums">{money(p.price, p.currency)}</p>
                {canPackages && (
                  <div className="mt-4 flex flex-wrap items-start gap-2 border-t pt-4">
                    <FormSheet triggerLabel={t("edit")} title={t("billing.editPackage", { code: p.code })} description={t("billing.packageEditHint")} variant="ghost" size="sm" icon="edit">
                      <ActionForm api={`billing/packages/${p.id}`} method="PUT" submitLabel={t("save")}>{packageFields(p)}</ActionForm>
                      <section className="mt-6 space-y-3">
                        <h3 className="text-sm font-semibold">{t("history.title")}</h3>
                        <ChangeHistory sources={[{ module: "billing", id: p.id }]} />
                      </section>
                    </FormSheet>
                    {p.isActive ? (
                      <ActionButton api={`billing/packages/${p.id}/deactivate`} label={t("deactivate")} variant="ghost" confirm={t("billing.deactivatePackageConfirm")} />
                    ) : (
                      <ActionButton api={`billing/packages/${p.id}/activate`} label={t("activate")} variant="ghost" />
                    )}
                  </div>
                )}
              </article>
            ))}
          </div>
        )}
      </div>
    </div>
  );
}
