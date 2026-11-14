import Link from "next/link";
import { getFormatter, getTranslations } from "next-intl/server";
import { apiFetch, requireMe } from "@konnyaku/session";
import { CreatePeriodForm, LockPeriodButton } from "./payroll-actions";

type Payslip = { teacherId: string; teacherName: string; currency: string; sessions: number; paidMinutes: number; amount: number; unpricedSessions: number };
type Period = { id: string; from: string; to: string; status: "Open" | "Locked"; payslips: Payslip[] };

export default async function PayrollPage() {
  const { token } = await requireMe("staff");
  const t = await getTranslations("payroll");
  const ops = await getTranslations("ops");
  const format = await getFormatter();
  const response = await apiFetch("/api/hr/payroll-periods", { token });
  const periods = response.ok ? ((await response.json()) as Period[]) : [];
  return (
    <main style={{ fontFamily: "system-ui", padding: 24 }}>
      <h1>{t("title")}</h1>
      <CreatePeriodForm />
      {periods.length === 0 && <p>{t("empty")}</p>}
      {periods.map((p) => (
        <section key={p.id}>
          <h2>{t("period", { from: p.from, to: p.to, status: t(`status.${p.status}`) })}</h2>
          <table>
            <thead>
              <tr>
                <th>{t("teacher")}</th>
                <th>{t("sessions")}</th>
                <th>{t("minutes")}</th>
                <th>{t("amount")}</th>
                <th>{t("unpriced")}</th>
              </tr>
            </thead>
            <tbody>
              {p.payslips.map((s) => (
                <tr key={`${s.teacherId}-${s.currency}`}>
                  <td>{s.teacherName}</td>
                  <td>{s.sessions}</td>
                  <td>{s.paidMinutes}</td>
                  <td>{s.currency ? format.number(s.amount, { style: "currency", currency: s.currency }) : null}</td>
                  <td>{s.unpricedSessions}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {p.status === "Open" && <LockPeriodButton periodId={p.id} />}
        </section>
      ))}
      <p><Link href="/">{ops("back")}</Link></p>
    </main>
  );
}
