import { getFormatter, getTranslations } from "next-intl/server";
import { FileText, ShieldCheck, TriangleAlert } from "lucide-react";
import type { Locale } from "@konnyaku/i18n";
import { tenantInfo } from "@konnyaku/session";
import { getPathname } from "@/i18n/navigation";
import { legalVersion } from "./legal";

type Block = string | string[];
type Section = { id: string; title: string; blocks: Block[] };

/** Privacy policy or terms of use: template text from the catalog with the center's name filled in. */
export async function LegalDocument({ kind, locale }: { kind: "privacy" | "terms"; locale: Locale }) {
  const t = await getTranslations("legal");
  const format = await getFormatter();
  const center = (await tenantInfo())?.name ?? "";
  const fill = (text: string) => text.replaceAll("{center}", center);
  const sections = t.raw(`${kind}.sections`) as Section[];
  const other = kind === "privacy" ? "terms" : "privacy";
  const date = format.dateTime(new Date(`${legalVersion.effectiveDate}T12:00:00Z`), { dateStyle: "long" });
  const Icon = kind === "privacy" ? ShieldCheck : FileText;

  return (
    <main className="mx-auto max-w-3xl px-4 py-12 sm:px-6 lg:py-16">
      <header className="space-y-4 border-b pb-8">
        <span className="grid size-12 place-items-center rounded-xl bg-primary/10 text-primary">
          <Icon className="size-6" aria-hidden />
        </span>
        <h1 className="text-3xl font-bold tracking-tight sm:text-4xl">{t(`${kind}.title`)}</h1>
        <p className="text-sm font-medium text-muted-foreground">{t("version", { version: legalVersion.version, date })}</p>
        <p className="flex gap-3 rounded-lg border border-warning/40 bg-warning/10 p-4 text-sm">
          <TriangleAlert className="mt-0.5 size-4 shrink-0 text-warning" aria-hidden />
          <span>{t("templateNotice", { center })}</span>
        </p>
        <p className="text-lg text-pretty text-muted-foreground">{t(`${kind}.intro`, { center })}</p>
      </header>

      <nav aria-label={t("contents")} className="my-8 rounded-xl border bg-card p-5">
        <p className="mb-3 text-sm font-semibold">{t("contents")}</p>
        <ol className="grid list-decimal gap-1.5 pl-5 text-sm text-muted-foreground sm:grid-cols-2">
          {sections.map((s) => (
            <li key={s.id}>
              <a href={`#${s.id}`} className="hover:text-foreground hover:underline">{s.title}</a>
            </li>
          ))}
        </ol>
      </nav>

      <div className="space-y-10">
        {sections.map((section, index) => (
          <section key={section.id} id={section.id} className="scroll-mt-24 space-y-3">
            <h2 className="text-xl font-semibold tracking-tight">
              <span className="mr-2 text-muted-foreground tabular-nums after:content-['.']">{index + 1}</span>
              {section.title}
            </h2>
            {section.blocks.map((block, i) =>
              Array.isArray(block) ? (
                <ul key={i} className="list-disc space-y-1.5 pl-5 text-pretty">
                  {block.map((item, j) => <li key={j}>{fill(item)}</li>)}
                </ul>
              ) : (
                <p key={i} className="text-pretty">{fill(block)}</p>
              ),
            )}
          </section>
        ))}
      </div>

      <footer className="mt-12 flex flex-wrap gap-1.5 border-t pt-6 text-sm text-muted-foreground">
        <span>{t("otherDocument")}</span>
        <a href={getPathname({ locale, href: `/${other}` })} className="font-medium text-primary hover:underline">
          {t(`${other}.title`)}
        </a>
      </footer>
    </main>
  );
}
