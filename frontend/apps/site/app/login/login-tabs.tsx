"use client";

import { useState, type FormEvent } from "react";
import { useTranslations } from "next-intl";

type Realm = "learn" | "staff";

export function LoginTabs({ initialRealm }: { initialRealm: Realm }) {
  const t = useTranslations("login");
  const [realm, setRealm] = useState<Realm>(initialRealm);
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  async function onSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setSubmitting(true);
    setError(null);
    const form = new FormData(event.currentTarget);
    const response = await fetch(`/api/session/${realm}`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(Object.fromEntries(form)),
    });
    const body = (await response.json()) as { redirect?: string; code?: string };
    if (response.ok && body.redirect) {
      // Full navigation: the target is another zone (separate Next.js app).
      window.location.assign(body.redirect);
      return;
    }
    const key = `errors.${(body.code ?? "unknown").replace(".", "_")}`;
    setError(t.has(key) ? t(key) : t("errors.unknown"));
    setSubmitting(false);
  }

  return (
    <div>
      <div role="tablist" style={{ display: "flex", gap: 8, marginBottom: 16 }}>
        {(["learn", "staff"] as const).map((r) => (
          <button key={r} role="tab" type="button" aria-selected={realm === r} onClick={() => setRealm(r)}
            style={{ fontWeight: realm === r ? 700 : 400 }}>
            {t(r === "learn" ? "learnTab" : "staffTab")}
          </button>
        ))}
      </div>
      <p>{t(realm === "learn" ? "learnHint" : "staffHint")}</p>
      <form key={realm} method="post" onSubmit={onSubmit} style={{ display: "grid", gap: 8 }}>
        <label>
          {t(realm === "learn" ? "username" : "email")}
          <input name={realm === "learn" ? "username" : "email"} type={realm === "learn" ? "text" : "email"}
            autoComplete="username" required />
        </label>
        <label>
          {t("password")}
          <input name="password" type="password" autoComplete="current-password" required />
        </label>
        {error && <p role="alert">{error}</p>}
        <button type="submit" disabled={submitting}>{t(submitting ? "submitting" : "submit")}</button>
      </form>
    </div>
  );
}
