"use client";

import { useState, type FormEvent } from "react";
import { useTranslations } from "next-intl";
import { useRouter } from "next/navigation";
import { Button } from "@konnyaku/ui/components/animate-ui/components/buttons/button";
import { Input } from "@konnyaku/ui/components/ui/input";
import { FormMessage } from "@konnyaku/ui/kk/blocks";
import { Field } from "@konnyaku/ui/kk/field";

export function ChangePasswordForm() {
  const t = useTranslations("password");
  const router = useRouter();
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState(false);

  async function onSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setPending(true);
    setError(null);
    const form = new FormData(event.currentTarget);
    const response = await fetch("/learn/api/password", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(Object.fromEntries(form)),
    });
    if (response.ok) {
      router.replace("/learn");
      router.refresh();
      return;
    }
    const body = (await response.json().catch(() => ({}))) as { code?: string };
    const key = `errors.${(body.code ?? "unknown").replaceAll(".", "_")}`;
    setError(t.has(key) ? t(key) : t("errors.unknown"));
    setPending(false);
  }

  return (
    <form method="post" onSubmit={onSubmit} className="grid gap-4">
      <Field label={t("current")}>
        <Input name="currentPassword" type="password" autoComplete="current-password" required />
      </Field>
      <Field label={t("next")}>
        <Input name="newPassword" type="password" autoComplete="new-password" minLength={8} required />
      </Field>
      {error && <FormMessage>{error}</FormMessage>}
      <Button type="submit" size="lg" disabled={pending}>{t("submit")}</Button>
    </form>
  );
}
