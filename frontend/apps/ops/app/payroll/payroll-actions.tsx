"use client";

import { useState, type FormEvent } from "react";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";

function useAction() {
  const t = useTranslations("payroll");
  const router = useRouter();
  const [error, setError] = useState<string | null>(null);
  async function run(path: string, body?: unknown) {
    setError(null);
    const response = await fetch(path, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: body ? JSON.stringify(body) : undefined,
    });
    if (response.ok) {
      router.refresh();
      return;
    }
    const data = (await response.json().catch(() => ({}))) as { code?: string };
    const key = `errors.${(data.code ?? "unknown").replace(".", "_")}`;
    setError(t.has(key) ? t(key) : t("errors.unknown"));
  }
  return { t, error, run };
}

export function CreatePeriodForm() {
  const { t, error, run } = useAction();
  async function onSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = new FormData(event.currentTarget);
    await run("/ops/api/payroll", { from: form.get("from"), to: form.get("to") });
  }
  return (
    <form method="post" onSubmit={onSubmit} style={{ display: "flex", gap: 8, alignItems: "end" }}>
      <label>{t("from")}<input name="from" type="date" required /></label>
      <label>{t("to")}<input name="to" type="date" required /></label>
      <button type="submit">{t("create")}</button>
      {error && <span role="alert">{error}</span>}
    </form>
  );
}

export function LockPeriodButton({ periodId }: { periodId: string }) {
  const { t, error, run } = useAction();
  return (
    <p>
      <button type="button" onClick={() => run(`/ops/api/payroll/${periodId}/lock`)}>{t("lock")}</button>
      {error && <span role="alert">{error}</span>}
    </p>
  );
}
