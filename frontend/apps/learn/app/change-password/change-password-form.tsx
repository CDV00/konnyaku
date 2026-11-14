"use client";

import { useState, type FormEvent } from "react";
import { useTranslations } from "next-intl";
import { useRouter } from "next/navigation";

export function ChangePasswordForm() {
  const t = useTranslations("password");
  const router = useRouter();
  const [error, setError] = useState<string | null>(null);

  async function onSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = new FormData(event.currentTarget);
    const response = await fetch("/learn/api/password", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(Object.fromEntries(form)),
    });
    if (response.ok) {
      router.replace("/");
      router.refresh();
      return;
    }
    const body = (await response.json().catch(() => ({}))) as { code?: string };
    const key = `errors.${(body.code ?? "unknown").replace(".", "_")}`;
    setError(t.has(key) ? t(key) : t("errors.unknown"));
  }

  return (
    <form method="post" onSubmit={onSubmit} style={{ display: "grid", gap: 8 }}>
      <label>
        {t("current")}
        <input name="currentPassword" type="password" autoComplete="current-password" required />
      </label>
      <label>
        {t("next")}
        <input name="newPassword" type="password" autoComplete="new-password" minLength={8} required />
      </label>
      {error && <p role="alert">{error}</p>}
      <button type="submit">{t("submit")}</button>
    </form>
  );
}
