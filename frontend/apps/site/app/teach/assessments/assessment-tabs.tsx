import Link from "next/link";
import { useTranslations } from "next-intl";
import { cn } from "@konnyaku/ui/lib/utils";

export type AssessmentTab = "assignments" | "banks" | "papers" | "grading";

const tabs: { key: AssessmentTab; href: string }[] = [
  { key: "assignments", href: "/teach/assessments" },
  { key: "banks", href: "/teach/assessments/banks" },
  { key: "papers", href: "/teach/assessments/papers" },
  { key: "grading", href: "/teach/assessments/grading" },
];

/** Sub-navigation of the teacher Assessment area (assignments, banks, papers, grading). */
export function AssessmentTabs({ current }: { current: AssessmentTab }) {
  const t = useTranslations("assessmentAuthoring.tabs");
  return (
    <nav aria-label={t("label")} className="-mx-1 overflow-x-auto">
      <ul className="flex w-max min-w-full gap-1 border-b px-1">
        {tabs.map((tab) => (
          <li key={tab.key}>
            <Link
              href={tab.href}
              aria-current={tab.key === current ? "page" : undefined}
              className={cn(
                "-mb-px inline-flex h-10 items-center border-b-2 px-3 text-sm font-medium whitespace-nowrap transition-colors",
                tab.key === current ? "border-primary text-foreground" : "border-transparent text-muted-foreground hover:text-foreground",
              )}
            >
              {t(tab.key)}
            </Link>
          </li>
        ))}
      </ul>
    </nav>
  );
}
