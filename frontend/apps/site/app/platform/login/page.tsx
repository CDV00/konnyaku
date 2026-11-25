import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import { ShieldCheck } from "lucide-react";
import { LogoMark } from "@konnyaku/ui/kk/logo";
import { PlatformLoginForm } from "./login-form";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("platform.login");
  return { title: t("title"), robots: { index: false, follow: false } };
}

export default async function PlatformLoginPage() {
  const t = await getTranslations("platform.login");
  return (
    <main className="relative isolate flex min-h-dvh items-center justify-center px-4 py-12">
      <div className="absolute inset-0 -z-10 bg-[radial-gradient(60%_50%_at_50%_0%,color-mix(in_oklch,var(--primary)_14%,transparent),transparent)]" />
      <div className="w-full max-w-md space-y-6">
        <div className="flex flex-col items-center gap-3 text-center">
          <span className="relative">
            <LogoMark className="size-12" />
            <ShieldCheck className="absolute -right-2 -bottom-1 size-5 rounded-full bg-background text-primary" />
          </span>
          <div className="space-y-1">
            <h1 className="text-2xl font-semibold tracking-tight">{t("title")}</h1>
            <p className="text-sm text-muted-foreground">{t("subtitle")}</p>
          </div>
        </div>
        <div className="rounded-2xl border bg-card p-6 shadow-sm">
          <PlatformLoginForm />
        </div>
      </div>
    </main>
  );
}
