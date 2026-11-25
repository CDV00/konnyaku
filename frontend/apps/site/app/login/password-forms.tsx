"use client";

import Link from "next/link";
import { useState, type FormEvent } from "react";
import { useTranslations } from "next-intl";
import { Button, buttonVariants } from "@konnyaku/ui/components/animate-ui/components/buttons/button";
import { Input } from "@konnyaku/ui/components/ui/input";
import { FormMessage } from "@konnyaku/ui/kk/blocks";
import { Field } from "@konnyaku/ui/kk/field";
import { postJson } from "../_account/two-factor";

function useErrorOf() {
  const t = useTranslations("login");
  return (code: string | undefined) => {
    const key = `errors.${(code ?? "unknown").replaceAll(".", "_")}`;
    return t.has(key) ? t(key) : t("errors.unknown");
  };
}

/** Asks for a reset link; the same answer whether the email has an account or not. */
export function ForgotForm() {
  const t = useTranslations("login.forgotPage");
  const errorOf = useErrorOf();
  const [sent, setSent] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState(false);

  async function onSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setPending(true);
    setError(null);
    const { ok, data } = await postJson("/api/password/forgot", { email: new FormData(event.currentTarget).get("email") });
    setPending(false);
    if (ok) setSent(true);
    else setError(errorOf(data.code));
  }

  if (sent) return <FormMessage tone="success">{t("sent")}</FormMessage>;
  return (
    <form onSubmit={onSubmit} className="grid gap-4">
      <Field label={t("email")}>
        <Input name="email" type="email" autoComplete="email" required autoFocus />
      </Field>
      {error && <FormMessage>{error}</FormMessage>}
      <Button type="submit" size="lg" disabled={pending}>{t("submit")}</Button>
    </form>
  );
}

/** New password from the emailed link (single use, 30 minutes). */
export function ResetForm({ token }: { token: string }) {
  const t = useTranslations("login.resetPage");
  const errorOf = useErrorOf();
  const [done, setDone] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState(false);

  async function onSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = new FormData(event.currentTarget);
    if (form.get("newPassword") !== form.get("confirm")) {
      setError(t("mismatch"));
      return;
    }
    setPending(true);
    setError(null);
    const { ok, data } = await postJson("/api/password/reset", { token, newPassword: form.get("newPassword") });
    setPending(false);
    if (ok) setDone(true);
    else setError(errorOf(data.code));
  }

  if (done) {
    return (
      <div className="grid gap-4">
        <FormMessage tone="success">{t("done")}</FormMessage>
        <Link href="/login?realm=staff" className={buttonVariants({ size: "lg" })}>{t("login")}</Link>
      </div>
    );
  }
  return (
    <form onSubmit={onSubmit} className="grid gap-4">
      <Field label={t("next")} hint={t("policy")}>
        <Input name="newPassword" type="password" autoComplete="new-password" minLength={8} required autoFocus />
      </Field>
      <Field label={t("confirm")}>
        <Input name="confirm" type="password" autoComplete="new-password" minLength={8} required />
      </Field>
      {error && <FormMessage>{error}</FormMessage>}
      <Button type="submit" size="lg" disabled={pending}>{t("submit")}</Button>
    </form>
  );
}
