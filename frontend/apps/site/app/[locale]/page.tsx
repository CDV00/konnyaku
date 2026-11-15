import { getLocale, getTranslations, setRequestLocale } from "next-intl/server";
import { ArrowRight, CalendarCheck, MessageCircleQuestion, PlayCircle, Sparkles } from "lucide-react";
import { apiFetch, tenantInfo } from "@konnyaku/session";
import { BubbleBackground } from "@konnyaku/ui/components/animate-ui/backgrounds/bubble";
import { Fade } from "@konnyaku/ui/components/animate-ui/primitives/effects/fade";
import { EmptyState } from "@konnyaku/ui/kk/blocks";

type Program = {
  id: string;
  code: string;
  name: Record<string, string>;
  languageCode: string;
  frameworkCode: string;
  levels: { id: string }[];
};

function localized(value: Record<string, string>, locale: string): string {
  return value[locale] ?? value[locale.split("-")[0] ?? ""] ?? Object.values(value)[0] ?? "";
}

/** Brand-tinted bubbles (RGB triplets): indigo, violet, coral, mint. */
const bubbles = {
  first: "99,102,241",
  second: "139,92,246",
  third: "56,189,248",
  fourth: "251,146,120",
  fifth: "52,211,153",
  sixth: "167,139,250",
};

const steps = [
  { key: "consult", icon: MessageCircleQuestion },
  { key: "placement", icon: CalendarCheck },
  { key: "learn", icon: PlayCircle },
] as const;

export default async function HomePage({ params }: { params: Promise<{ locale: string }> }) {
  const { locale } = await params;
  setRequestLocale(locale);
  const t = await getTranslations("site");
  const response = await apiFetch("/api/academic/public/programs");
  const programs = response.ok ? ((await response.json()) as Program[]) : [];
  const uiLocale = await getLocale();
  const tenant = await tenantInfo();
  const center = tenant?.name ?? "";
  const languages = new Set(programs.map((p) => p.languageCode)).size;

  return (
    <main>
      <section className="relative isolate overflow-hidden">
        <BubbleBackground colors={bubbles} className="absolute inset-0 -z-10 opacity-35 dark:opacity-25" />
        <div className="absolute inset-0 -z-10 bg-gradient-to-b from-background/40 via-background/70 to-background" />
        <div className="mx-auto max-w-6xl px-4 pt-20 pb-24 sm:px-6 sm:pt-28 sm:pb-32">
          <Fade inView className="max-w-2xl space-y-6">
            <span className="inline-flex items-center gap-2 rounded-full border bg-card/70 px-3 py-1 text-xs font-medium text-muted-foreground backdrop-blur">
              <Sparkles className="size-3.5 text-highlight" />
              {t("heroEyebrow", { count: languages })}
            </span>
            <h1 className="text-4xl leading-tight font-bold tracking-tight text-balance sm:text-5xl">
              {t.rich("heroTitle", {
                center,
                hl: (chunks) => <span className="bg-gradient-to-r from-primary to-highlight bg-clip-text text-transparent">{chunks}</span>,
              })}
            </h1>
            <p className="text-lg text-pretty text-muted-foreground">{t("heroBody")}</p>
            <div className="flex flex-wrap gap-3">
              <a
                href={`/${locale}/register`}
                className="group inline-flex h-11 items-center gap-2 rounded-lg bg-primary px-5 font-medium text-primary-foreground shadow-sm transition-all hover:bg-primary/90 hover:shadow-md"
              >
                {t("register")}
                <ArrowRight className="size-4 transition-transform group-hover:translate-x-0.5" />
              </a>
              <a href="#programs" className="inline-flex h-11 items-center rounded-lg border bg-card/80 px-5 font-medium backdrop-blur transition-colors hover:bg-accent">
                {t("explore")}
              </a>
            </div>
          </Fade>
        </div>
      </section>

      <section id="programs" className="mx-auto max-w-6xl scroll-mt-20 px-4 py-16 sm:px-6">
        <div className="mb-8 max-w-2xl space-y-2">
          <h2 className="text-2xl font-semibold tracking-tight sm:text-3xl">{t("programs")}</h2>
          <p className="text-muted-foreground">{t("programsBody")}</p>
        </div>
        {programs.length === 0 ? (
          <EmptyState title={t("noPrograms")} />
        ) : (
          <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
            {programs.map((p, index) => (
              <Fade key={p.id} inView delay={index * 80}>
                <article className="group flex h-full flex-col rounded-xl border bg-card p-5 shadow-xs transition-all hover:-translate-y-0.5 hover:border-primary/40 hover:shadow-md">
                  <div className="flex items-center justify-between gap-2">
                    <span className="grid size-11 place-items-center rounded-lg bg-primary/10 text-sm font-bold text-primary uppercase">
                      {p.languageCode.split("-")[0]}
                    </span>
                    <span className="rounded-full bg-muted px-2.5 py-0.5 text-xs font-medium text-muted-foreground">{p.frameworkCode}</span>
                  </div>
                  <h3 className="mt-4 text-lg font-semibold">{localized(p.name, uiLocale)}</h3>
                  <p className="mt-1 text-sm text-muted-foreground">{t("levels", { count: p.levels.length })}</p>
                  <a
                    href={`/${locale}/register?language=${encodeURIComponent(p.languageCode)}`}
                    className="mt-6 inline-flex items-center gap-1.5 text-sm font-medium text-primary"
                  >
                    {t("consultThis")}
                    <ArrowRight className="size-4 transition-transform group-hover:translate-x-1" />
                  </a>
                </article>
              </Fade>
            ))}
          </div>
        )}
      </section>

      <section className="border-y bg-muted/40">
        <div className="mx-auto max-w-6xl px-4 py-16 sm:px-6">
          <h2 className="mb-8 text-2xl font-semibold tracking-tight sm:text-3xl">{t("howTitle")}</h2>
          <ol className="grid gap-4 md:grid-cols-3">
            {steps.map(({ key, icon: Icon }, index) => (
              <Fade key={key} inView delay={index * 120}>
                <li className="h-full rounded-xl border bg-card p-5 shadow-xs">
                  <div className="flex items-center gap-3">
                    <span className="grid size-10 place-items-center rounded-full bg-primary text-primary-foreground">
                      <Icon className="size-5" />
                    </span>
                    <span className="text-sm font-semibold text-muted-foreground">{t("step", { number: index + 1 })}</span>
                  </div>
                  <h3 className="mt-4 font-semibold">{t(`steps.${key}.title`)}</h3>
                  <p className="mt-1 text-sm text-muted-foreground">{t(`steps.${key}.body`)}</p>
                </li>
              </Fade>
            ))}
          </ol>
        </div>
      </section>

      <section className="mx-auto max-w-6xl px-4 py-16 sm:px-6">
        <div className="flex flex-col items-start gap-4 rounded-2xl bg-gradient-to-br from-primary to-primary/80 p-8 text-primary-foreground shadow-lg sm:flex-row sm:items-center sm:justify-between sm:p-10">
          <div className="space-y-1">
            <h2 className="text-2xl font-semibold tracking-tight">{t("ctaTitle")}</h2>
            <p className="text-primary-foreground/80">{t("ctaBody")}</p>
          </div>
          <a
            href={`/${locale}/register`}
            className="inline-flex h-11 shrink-0 items-center gap-2 rounded-lg bg-background px-5 font-medium text-foreground shadow-sm transition-colors hover:bg-background/90"
          >
            {t("register")}
            <ArrowRight className="size-4" />
          </a>
        </div>
      </section>
    </main>
  );
}
