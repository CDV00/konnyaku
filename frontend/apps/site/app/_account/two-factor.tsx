"use client";

import { useEffect, useRef, useState, type FormEvent } from "react";
import { useTranslations } from "next-intl";
import { Check, Copy } from "lucide-react";
import { Button } from "@konnyaku/ui/components/animate-ui/components/buttons/button";
import { Input } from "@konnyaku/ui/components/ui/input";
import { FormMessage } from "@konnyaku/ui/kk/blocks";
import { Field } from "@konnyaku/ui/kk/field";

/** Translates an API error code (`identity.code_invalid` → `errors.identity_code_invalid`) of the account namespace. */
export function useAccountError() {
  const t = useTranslations("account");
  return (code: string | undefined) => {
    const key = `errors.${(code ?? "unknown").replaceAll(".", "_")}`;
    return t.has(key) ? t(key) : t("errors.unknown");
  };
}

/** POSTs JSON and returns the body, or `{ code }` with ok=false. */
export async function postJson<T = Record<string, unknown>>(url: string, body: unknown): Promise<{ ok: boolean; data: T & { code?: string } }> {
  const response = await fetch(url, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body ?? {}) });
  const data = (await response.json().catch(() => ({}))) as T & { code?: string };
  return { ok: response.ok, data };
}

/** The single-use recovery codes, shown once right after they are made. */
export function RecoveryCodes({ codes }: { codes: string[] }) {
  const t = useTranslations("account.twoFactor");
  const [copied, setCopied] = useState(false);
  return (
    <div className="grid gap-3 rounded-lg border border-warning/40 bg-warning/10 p-4">
      <p className="text-sm font-medium">{t("codesTitle")}</p>
      <p className="text-xs text-muted-foreground">{t("codesHint")}</p>
      <ul className="grid grid-cols-2 gap-2 font-mono text-sm">
        {codes.map((code) => (
          <li key={code} className="rounded bg-background px-2 py-1 text-center">{code}</li>
        ))}
      </ul>
      <Button
        type="button"
        variant="outline"
        size="sm"
        onClick={() => {
          void navigator.clipboard?.writeText(codes.join("\n"));
          setCopied(true);
        }}
      >
        {copied ? <Check /> : <Copy />}
        {t(copied ? "copied" : "copy")}
      </Button>
    </div>
  );
}

/**
 * Enrolling an authenticator app: asks for a new secret (QR + text), then the first code. Used at sign-in (roles that
 * must use 2FA) and on the account page.
 */
export function TwoFactorEnrollment({ setupUrl, setupBody, enableUrl, enableBody, onEnabled }: {
  setupUrl: string;
  setupBody?: Record<string, unknown>;
  enableUrl: string;
  enableBody?: Record<string, unknown>;
  onEnabled: (data: { recoveryCodes: string[]; redirect?: string }) => void;
}) {
  const t = useTranslations("account.twoFactor");
  const errorOf = useAccountError();
  const [setup, setSetup] = useState<{ secret: string; qr: string } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState(false);

  // One secret per enrollment: a second setup call would replace the pending secret on the server, so the guard also
  // covers React re-running the effect (Strict Mode in development).
  const started = useRef(false);
  useEffect(() => {
    if (started.current) return;
    started.current = true;
    void postJson<{ secret: string; qr: string }>(setupUrl, setupBody).then(({ ok, data }) => {
      if (ok) setSetup(data);
      else setError(errorOf(data.code));
    });
  }, [setupUrl, setupBody, errorOf]);

  async function onSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setPending(true);
    setError(null);
    const code = String(new FormData(event.currentTarget).get("code") ?? "");
    const { ok, data } = await postJson<{ recoveryCodes: string[]; redirect?: string }>(enableUrl, { ...enableBody, code });
    setPending(false);
    if (ok) onEnabled(data);
    else setError(errorOf(data.code));
  }

  return (
    <form onSubmit={onSubmit} className="grid gap-4">
      <p className="text-sm text-muted-foreground">{t("scan")}</p>
      {setup && (
        <div className="flex flex-col items-center gap-3 sm:flex-row sm:items-start">
          {/* eslint-disable-next-line @next/next/no-img-element -- a generated data URL, nothing to optimize */}
          <img src={setup.qr} alt={t("qrAlt")} className="size-40 rounded-lg border bg-white p-1" />
          <div className="grid min-w-0 gap-1 text-sm">
            <span className="text-muted-foreground">{t("secret")}</span>
            <code className="rounded bg-muted px-2 py-1 font-mono text-xs break-all">{setup.secret}</code>
          </div>
        </div>
      )}
      <Field label={t("code")}>
        <Input name="code" inputMode="numeric" autoComplete="one-time-code" pattern="[0-9 ]{6,7}" maxLength={7} required />
      </Field>
      {error && <FormMessage>{error}</FormMessage>}
      <Button type="submit" disabled={pending || !setup}>{t("verify")}</Button>
    </form>
  );
}
