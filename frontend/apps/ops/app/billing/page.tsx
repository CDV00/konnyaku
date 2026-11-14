import { getFormatter, getLocale, getTranslations } from "next-intl/server";
import { requireMe } from "@konnyaku/session";
import { ActionForm } from "../action-form";
import { getJson, localized, type ClassRow, type Package, type Student } from "../lib";

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
  const packageName = new Map(packages.map((p) => [p.id, `${p.code} · ${localized(p.name, locale)}`]));
  const className = new Map(classes.map((c) => [c.id, c.code]));

  return (
    <main>
      <h1>{t("billing.title")}</h1>

      <h2>{t("billing.orders")}</h2>
      <details className="panel">
        <summary>{t("billing.createOrder")}</summary>
        <ActionForm api="billing/orders" submitLabel={t("save")}>
          <label>
            {t("billing.student")}
            <select name="studentId" required>
              {students.map((s) => <option key={s.id} value={s.id}>{s.fullName}</option>)}
            </select>
          </label>
          <label>
            {t("billing.package")}
            <select name="packageId" required>
              {packages.map((p) => <option key={p.id} value={p.id}>{t("billing.packageOption", { name: packageName.get(p.id) ?? p.code, price: money(p.price, p.currency) })}</option>)}
            </select>
          </label>
          <label>
            {t("billing.class")}
            <select name="classId">
              <option value="">{t("billing.noClass")}</option>
              {classes.filter((c) => c.status === "Open").map((c) => <option key={c.id} value={c.id}>{c.code}</option>)}
            </select>
          </label>
        </ActionForm>
      </details>
      {orders.length === 0 ? (
        <p className="muted">{t("billing.noOrders")}</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>{t("billing.student")}</th>
              <th>{t("billing.package")}</th>
              <th>{t("billing.class")}</th>
              <th className="num">{t("billing.amount")}</th>
              <th className="num">{t("billing.received")}</th>
              <th>{t("fields.status")}</th>
              <th>{t("billing.payment")}</th>
            </tr>
          </thead>
          <tbody>
            {orders.map((o) => (
              <tr key={o.id}>
                <td>{studentName.get(o.studentId) ?? o.studentId}</td>
                <td>{packageName.get(o.packageId)}</td>
                <td>{o.classId ? className.get(o.classId) : null}</td>
                <td className="num">{money(o.amount, o.currency)}</td>
                <td className="num">{money(o.amountReceived, o.currency)}</td>
                <td>{t(`orderStatus.${o.status}`)}</td>
                <td>
                  {o.status === "PendingPayment" && (
                    <ActionForm api={`billing/orders/${o.id}/payments`} submitLabel={t("billing.recordPayment")}>
                      <input
                        name="amount"
                        type="number"
                        min={1}
                        required
                        defaultValue={o.amount - o.amountReceived}
                        className="compact"
                        aria-label={t("billing.amount")}
                      />
                      <select name="method" aria-label={t("billing.method")}>
                        {paymentMethods.map((m) => <option key={m} value={m}>{t(`billing.methods.${m}`)}</option>)}
                      </select>
                      <input name="reference" placeholder={t("billing.reference")} className="compact" />
                    </ActionForm>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      <h2>{t("billing.packages")}</h2>
      {packages.length === 0 ? (
        <p className="muted">{t("billing.noPackages")}</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>{t("fields.code")}</th>
              <th>{t("fields.name")}</th>
              <th className="num">{t("billing.credits")}</th>
              <th className="num">{t("billing.price")}</th>
            </tr>
          </thead>
          <tbody>
            {packages.map((p) => (
              <tr key={p.id}>
                <td>{p.code}</td>
                <td>{localized(p.name, locale)}</td>
                <td className="num">{t("billing.creditsValue", { paid: p.paidCredits, bonus: p.bonusCredits })}</td>
                <td className="num">{money(p.price, p.currency)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      <details className="panel">
        <summary>{t("billing.createPackage")}</summary>
        <ActionForm api="billing/packages" submitLabel={t("save")}>
          <label>{t("fields.code")}<input name="code" required className="compact" /></label>
          <label>{t("fields.nameVi")}<input name="name.vi" required /></label>
          <label>{t("fields.nameEn")}<input name="name.en" /></label>
          <label>{t("billing.paidCredits")}<input name="paidCredits" type="number" min={1} required className="compact" /></label>
          <label>{t("billing.bonusCredits")}<input name="bonusCredits" type="number" min={0} defaultValue={0} className="compact" /></label>
          <label>{t("billing.price")}<input name="price" type="number" min={0} step={1000} required /></label>
          <label>{t("fields.currency")}<input name="currency" required defaultValue="VND" size={3} /></label>
        </ActionForm>
      </details>
    </main>
  );
}
