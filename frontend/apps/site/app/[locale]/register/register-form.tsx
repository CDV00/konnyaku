"use client";

import { useState, type FormEvent } from "react";
import { useTranslations } from "next-intl";

const consentVersion = "2026-11";

export function RegisterForm({ languages, locale }: { languages: { code: string; label: string }[]; locale: string }) {
  const t = useTranslations("register");
  const [state, setState] = useState<"idle" | "sending" | "sent">("idle");
  const [error, setError] = useState<string | null>(null);

  async function onSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setState("sending");
    setError(null);
    const form = new FormData(event.currentTarget);
    const text = (name: string) => {
      const value = String(form.get(name) ?? "").trim();
      return value === "" ? null : value;
    };
    const utm = Object.fromEntries([...new URLSearchParams(window.location.search)].filter(([k]) => k.startsWith("utm_")));
    const response = await fetch("/api/leads", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        studentName: text("studentName") ?? "",
        birthYear: text("birthYear") ? Number(text("birthYear")) : null,
        guardianName: text("guardianName"),
        phone: text("phone"),
        email: text("email"),
        languageCode: text("languageCode") ?? "",
        goal: text("goal"),
        preferredSchedule: text("preferredSchedule"),
        consentGiven: form.get("consent") === "on",
        consentVersion,
        locale,
        utm,
        website: text("website"),
      }),
    });
    if (response.ok) {
      setState("sent");
      return;
    }
    const body = (await response.json().catch(() => ({}))) as { code?: string };
    const key = `errors.${(body.code ?? "unknown").replace(".", "_")}`;
    setError(t.has(key) ? t(key) : t("errors.unknown"));
    setState("idle");
  }

  if (state === "sent") return <p role="status">{t("sent")}</p>;

  return (
    <form method="post" onSubmit={onSubmit} style={{ display: "grid", gap: 8 }}>
      <label>{t("studentName")}<input name="studentName" required /></label>
      <label>{t("birthYear")}<input name="birthYear" type="number" min={1900} max={2100} /></label>
      <label>{t("guardianName")}<input name="guardianName" /></label>
      <label>{t("phone")}<input name="phone" type="tel" /></label>
      <label>{t("email")}<input name="email" type="email" /></label>
      <label>
        {t("language")}
        <select name="languageCode" required defaultValue="">
          <option value="" disabled />
          {languages.map((l) => (
            <option key={l.code} value={l.code}>{l.label}</option>
          ))}
        </select>
      </label>
      <label>{t("goal")}<input name="goal" /></label>
      <label>{t("schedule")}<input name="preferredSchedule" /></label>
      <input name="website" tabIndex={-1} autoComplete="off" aria-hidden="true" style={{ position: "absolute", left: -10000 }} />
      <label><input name="consent" type="checkbox" required /> {t("consent")}</label>
      {error && <p role="alert">{error}</p>}
      <button type="submit" disabled={state === "sending"}>{t("submit")}</button>
    </form>
  );
}
