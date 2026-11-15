import Link from "next/link";
import { getTranslations } from "next-intl/server";
import { ArrowUpRight, CalendarDays, GraduationCap, PhoneCall, Receipt } from "lucide-react";
import { requireMe } from "@konnyaku/session";
import { Fade } from "@konnyaku/ui/components/animate-ui/primitives/effects/fade";
import { PageHeader, StatCard } from "@konnyaku/ui/kk/blocks";
import { getJson, type ClassRow, type Student } from "./lib";

const sections = ["leads", "students", "billing", "classes", "programs", "staff", "branches", "payroll"] as const;

export default async function HomePage() {
  const { me, token } = await requireMe("staff");
  const t = await getTranslations("ops");
  const session = await getTranslations("session");
  const [leads, students, classes, orders] = await Promise.all([
    getJson<unknown[]>("/api/crm/leads", token, []),
    getJson<Student[]>("/api/crm/students", token, []),
    getJson<ClassRow[]>("/api/academic/classes", token, []),
    getJson<{ status: string }[]>("/api/billing/orders", token, []),
  ]);
  const running = classes.filter((c) => c.status === "Open" || c.status === "Running").length;
  const unpaid = orders.filter((o) => o.status === "PendingPayment").length;

  return (
    <div className="space-y-8">
      <PageHeader title={session("greeting", { name: me.displayName })} description={t("subtitle")} />
      <Fade inView className="grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
        <StatCard label={t("stats.leads")} count={leads.length} icon={PhoneCall} tone="highlight" hint={t("stats.leadsHint")} />
        <StatCard label={t("stats.students")} count={students.length} icon={GraduationCap} hint={t("stats.studentsHint")} />
        <StatCard label={t("stats.classes")} count={running} icon={CalendarDays} tone="success" hint={t("stats.classesHint")} />
        <StatCard label={t("stats.unpaid")} count={unpaid} icon={Receipt} tone={unpaid > 0 ? "warning" : "muted"} hint={t("stats.unpaidHint")} />
      </Fade>
      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
        {sections.map((section) => (
          <Link
            key={section}
            href={`/ops/${section}`}
            className="group rounded-xl border bg-card p-4 shadow-xs transition-all hover:-translate-y-0.5 hover:border-primary/40 hover:shadow-md"
          >
            <span className="flex items-center justify-between font-medium">
              {t(`nav.${section}`)}
              <ArrowUpRight className="size-4 text-muted-foreground transition-transform group-hover:translate-x-0.5 group-hover:-translate-y-0.5 group-hover:text-primary" />
            </span>
            <span className="mt-1 block text-sm text-muted-foreground">{t(`hint.${section}`)}</span>
          </Link>
        ))}
      </div>
    </div>
  );
}
