import { getTranslations } from "next-intl/server";
import { ShieldCheck } from "lucide-react";
import { requireMe } from "@konnyaku/session";
import { ChangePasswordForm } from "./change-password-form";

export default async function ChangePasswordPage({ searchParams }: { searchParams: Promise<{ required?: string }> }) {
  await requireMe("learn");
  const { required } = await searchParams;
  const t = await getTranslations("password");
  return (
    <div className="mx-auto max-w-md space-y-6 py-6">
      <div className="flex flex-col items-center gap-3 text-center">
        <span className="grid size-12 place-items-center rounded-full bg-primary/10 text-primary">
          <ShieldCheck className="size-6" />
        </span>
        <h1 className="text-2xl font-semibold tracking-tight">{t("title")}</h1>
        <p className="text-sm text-muted-foreground">{t(required === "1" ? "required" : "hint")}</p>
      </div>
      <div className="rounded-2xl border bg-card p-6 shadow-sm">
        <ChangePasswordForm />
      </div>
    </div>
  );
}
