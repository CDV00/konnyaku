import type { Metadata } from "next";
import { hasLocale } from "next-intl";
import { getTranslations } from "next-intl/server";
import { CheckCircle2 } from "lucide-react";
import { apiFetch, tenantInfo } from "@konnyaku/session";
import { getPathname } from "@/i18n/navigation";
import { routing } from "@/i18n/routing";
import { pageMetadata } from "../../seo";
import { publicLocale } from "../guard";
import { RegisterForm } from "./register-form";

type Language = { code: string; name: Record<string, string> };

const perks = ["callback", "trial", "flexible"] as const;

export async function generateMetadata({ params }: { params: Promise<{ locale: string }> }): Promise<Metadata> {
  const { locale } = await params;
  if (!hasLocale(routing.locales, locale)) return {};
  const t = await getTranslations({ locale, namespace: "status.seo" });
  const tenant = await tenantInfo();
  return pageMetadata({
    locale,
    href: "/register",
    title: t("registerTitle"),
    description: t("registerDescription", { center: tenant?.name ?? "" }),
    tenant,
  });
}

export default async function RegisterPage({ params, searchParams }: {
  params: Promise<{ locale: string }>;
  searchParams: Promise<{ language?: string }>;
}) {
  const locale = await publicLocale(params);
  const { language } = await searchParams;
  const t = await getTranslations("register");
  const tenant = await tenantInfo();
  const privacyHref = getPathname({ locale, href: "/privacy" });
  const response = await apiFetch("/api/catalog/languages");
  const languages = response.ok ? ((await response.json()) as Language[]) : [];
  const options = languages.map((l) => ({ code: l.code, label: l.name[locale] ?? l.name.en ?? l.code }));
  return (
    <main className="mx-auto grid max-w-6xl gap-10 px-4 py-12 sm:px-6 lg:grid-cols-[1fr_1.15fr] lg:py-16">
      <div className="space-y-6 lg:pt-6">
        <div className="space-y-3">
          <h1 className="text-3xl font-bold tracking-tight sm:text-4xl">{t("title")}</h1>
          <p className="text-lg text-muted-foreground">{t("intro")}</p>
        </div>
        <ul className="space-y-3">
          {perks.map((perk) => (
            <li key={perk} className="flex gap-3">
              <CheckCircle2 className="mt-0.5 size-5 shrink-0 text-success" />
              <span>{t(`perks.${perk}`)}</span>
            </li>
          ))}
        </ul>
      </div>
      <div className="rounded-2xl border bg-card p-6 shadow-sm sm:p-8">
        <RegisterForm
          languages={options}
          locale={locale}
          defaultLanguage={options.some((o) => o.code === language) ? language : undefined}
          center={tenant?.name ?? ""}
          privacyHref={privacyHref}
        />
      </div>
    </main>
  );
}
