import { getTranslations } from "next-intl/server";
import { requireMe } from "@konnyaku/session";
import { Onboarding, type OnboardingStep } from "@konnyaku/ui/kk/onboarding";
import { getJson } from "./lib";

const allSteps = ["branches", "staff", "programs", "classes", "students", "billing", "payroll", "leads"] as const;
type Step = (typeof allSteps)[number];

/** Which setup steps each role is responsible for, in that role's working order; several roles get the union. */
const stepsByRole: Record<string, readonly Step[]> = {
  TenantAdmin: allSteps,
  BranchManager: ["branches", "classes", "students"],
  Coordinator: ["classes", "students"],
  Consultant: ["leads", "students"],
  Accountant: ["billing", "payroll"],
};

/** First-run checklist on the /ops home page, ticked off from the center's real data. */
export async function OpsOnboarding() {
  const { me, token } = await requireMe("staff");
  const mine = [...new Set(me.roles.flatMap((role) => stepsByRole[role] ?? []))];
  if (mine.length === 0) return null;

  const t = await getTranslations("onboarding");
  const [branches, staff, programs, classes, students, orders, periods] = await Promise.all([
    getJson<unknown[]>("/api/organization/branches", token, []),
    getJson<unknown[]>("/api/identity/staff", token, []),
    getJson<unknown[]>("/api/academic/programs", token, []),
    getJson<unknown[]>("/api/academic/classes", token, []),
    getJson<unknown[]>("/api/crm/students", token, []),
    getJson<unknown[]>("/api/billing/orders", token, []),
    getJson<unknown[]>("/api/hr/payroll-periods", token, []),
  ]);
  const done: Record<Step, boolean | undefined> = {
    branches: branches.length > 0,
    staff: staff.length > 1,
    programs: programs.length > 0,
    classes: classes.length > 0,
    students: students.length > 0,
    billing: orders.length > 0,
    payroll: periods.length > 0,
    leads: undefined,
  };
  const steps: OnboardingStep[] = mine.map((step) => ({
    key: step,
    title: t(`ops.steps.${step}.title`),
    description: t(`ops.steps.${step}.description`),
    href: `/ops/${step}`,
    done: done[step],
  }));
  const tracked = steps.filter((s) => s.done !== undefined);

  return (
    <Onboarding
      storageKey={`ops:${me.userId}`}
      title={t("ops.title")}
      intro={t("ops.intro")}
      steps={steps}
      labels={{
        show: t("show"),
        hide: t("hide"),
        progress: tracked.length > 0 ? t("progress", { done: tracked.filter((s) => s.done).length, total: tracked.length }) : undefined,
      }}
    />
  );
}
