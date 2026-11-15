import { getTranslations } from "next-intl/server";
import { tenantInfo } from "@konnyaku/session";
import { LogoMark } from "@konnyaku/ui/kk/logo";
import { LoginTabs } from "./login-tabs";

export default async function LoginPage({ searchParams }: { searchParams: Promise<{ realm?: string }> }) {
  const { realm } = await searchParams;
  const t = await getTranslations("login");
  const tenant = await tenantInfo();
  return (
    <main className="relative isolate flex min-h-[calc(100dvh-4rem)] items-center justify-center px-4 py-12">
      <div className="absolute inset-0 -z-10 bg-[radial-gradient(60%_50%_at_50%_0%,color-mix(in_oklch,var(--primary)_14%,transparent),transparent)]" />
      <div className="w-full max-w-md space-y-6">
        <div className="flex flex-col items-center gap-3 text-center">
          <LogoMark className="size-12" />
          <div className="space-y-1">
            <h1 className="text-2xl font-semibold tracking-tight">{t("title")}</h1>
            {tenant && <p className="text-sm text-muted-foreground">{t("subtitle", { center: tenant.name })}</p>}
          </div>
        </div>
        <div className="rounded-2xl border bg-card p-6 shadow-sm">
          <LoginTabs initialRealm={realm === "staff" ? "staff" : "learn"} />
        </div>
      </div>
    </main>
  );
}
