"use client";

import { useState, type FormEvent } from "react";
import { useFormatter, useTranslations } from "next-intl";
import { useRouter } from "next/navigation";
import { Laptop, ShieldCheck, ShieldOff } from "lucide-react";
import { Button } from "@konnyaku/ui/components/animate-ui/components/buttons/button";
import { Input } from "@konnyaku/ui/components/ui/input";
import { FormMessage, Panel, StatusBadge } from "@konnyaku/ui/kk/blocks";
import { Field } from "@konnyaku/ui/kk/field";
import { RecoveryCodes, TwoFactorEnrollment, postJson, useAccountError } from "./two-factor";

export type SessionItem = {
  id: string;
  audience: "staff" | "learn";
  createdAt: string;
  lastSeenAt: string;
  expiresAt: string;
  userAgent: string | null;
  ipAddress: string | null;
  current: boolean;
};

export type MySecurity = {
  twoFactorEnabled: boolean;
  twoFactorRequired: boolean;
  recoveryCodesLeft: number;
  mustChangePassword: boolean;
  sessions: SessionItem[];
};

const api = (path: string) => `/api/account/identity/${path}`;

/** A short device name from the browser's user agent (no library: the common browsers and systems only). */
function device(userAgent: string | null): string | null {
  if (!userAgent) return null;
  const browser = /Edg\//.test(userAgent) ? "Edge" : /Chrome\//.test(userAgent) ? "Chrome" : /Firefox\//.test(userAgent) ? "Firefox" : /Safari\//.test(userAgent) ? "Safari" : null;
  const system = /Windows/.test(userAgent) ? "Windows" : /Android/.test(userAgent) ? "Android" : /iPhone|iPad/.test(userAgent) ? "iOS" : /Mac OS X/.test(userAgent) ? "macOS" : /Linux/.test(userAgent) ? "Linux" : null;
  return [browser, system].filter(Boolean).join(" · ") || null;
}

export function ChangePasswordPanel({ required, home }: { required: boolean; home: string }) {
  const t = useTranslations("account.password");
  const errorOf = useAccountError();
  const router = useRouter();
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState(false);
  const [pending, setPending] = useState(false);

  async function onSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const formElement = event.currentTarget;
    const form = new FormData(formElement);
    if (form.get("newPassword") !== form.get("confirm")) {
      setError(t("mismatch"));
      return;
    }
    setPending(true);
    setError(null);
    setDone(false);
    const { ok, data } = await postJson(api("staff/change-password"), {
      currentPassword: form.get("currentPassword"),
      newPassword: form.get("newPassword"),
    });
    setPending(false);
    if (!ok) {
      setError(errorOf(data.code));
      return;
    }
    if (required) {
      window.location.assign(home);
      return;
    }
    formElement.reset();
    setDone(true);
    router.refresh();
  }

  return (
    <Panel title={t("title")} description={t("hint")}>
      <form onSubmit={onSubmit} className="grid max-w-md gap-4">
        {required && <FormMessage>{t("required")}</FormMessage>}
        <Field label={t("current")}>
          <Input name="currentPassword" type="password" autoComplete="current-password" required />
        </Field>
        <Field label={t("next")} hint={t("policy")}>
          <Input name="newPassword" type="password" autoComplete="new-password" minLength={8} required />
        </Field>
        <Field label={t("confirm")}>
          <Input name="confirm" type="password" autoComplete="new-password" minLength={8} required />
        </Field>
        {error && <FormMessage>{error}</FormMessage>}
        {done && <FormMessage tone="success">{t("done")}</FormMessage>}
        <Button type="submit" disabled={pending} className="justify-self-start">{t("submit")}</Button>
      </form>
    </Panel>
  );
}

export function TwoFactorPanel({ security }: { security: MySecurity }) {
  const t = useTranslations("account.twoFactor");
  const errorOf = useAccountError();
  const router = useRouter();
  const [mode, setMode] = useState<"idle" | "enroll" | "disable" | "regenerate">("idle");
  const [codes, setCodes] = useState<string[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState(false);

  async function submit(event: FormEvent<HTMLFormElement>, path: string) {
    event.preventDefault();
    setPending(true);
    setError(null);
    const body = Object.fromEntries(new FormData(event.currentTarget));
    const { ok, data } = await postJson<{ recoveryCodes?: string[] }>(api(path), body);
    setPending(false);
    if (!ok) {
      setError(errorOf(data.code));
      return;
    }
    setMode("idle");
    if (data.recoveryCodes) setCodes(data.recoveryCodes);
    router.refresh();
  }

  const status = security.twoFactorEnabled ? (
    <StatusBadge tone="success"><ShieldCheck className="size-3" />{t("on")}</StatusBadge>
  ) : (
    <StatusBadge tone={security.twoFactorRequired ? "destructive" : "muted"}><ShieldOff className="size-3" />{t("off")}</StatusBadge>
  );

  return (
    <Panel title={t("title")} description={t("description")} action={status}>
      <div className="grid max-w-xl gap-4">
        {security.twoFactorRequired && <p className="text-sm text-muted-foreground">{t("requiredHint")}</p>}
        {codes && <RecoveryCodes codes={codes} />}
        {!security.twoFactorEnabled && mode !== "enroll" && (
          <Button type="button" className="justify-self-start" onClick={() => { setCodes(null); setMode("enroll"); }}>{t("enable")}</Button>
        )}
        {!security.twoFactorEnabled && mode === "enroll" && (
          <TwoFactorEnrollment
            setupUrl={api("me/2fa/setup")}
            enableUrl={api("me/2fa/enable")}
            onEnabled={(data) => {
              setCodes(data.recoveryCodes);
              setMode("idle");
              router.refresh();
            }}
          />
        )}
        {security.twoFactorEnabled && (
          <>
            <p className="text-sm text-muted-foreground">{t("codesLeft", { count: security.recoveryCodesLeft })}</p>
            <div className="flex flex-wrap gap-2">
              <Button type="button" variant="outline" size="sm" onClick={() => { setError(null); setMode(mode === "regenerate" ? "idle" : "regenerate"); }}>
                {t("regenerate")}
              </Button>
              {!security.twoFactorRequired && (
                <Button type="button" variant="ghost" size="sm" onClick={() => { setError(null); setMode(mode === "disable" ? "idle" : "disable"); }}>
                  {t("disable")}
                </Button>
              )}
            </div>
            {mode === "regenerate" && (
              <form onSubmit={(e) => submit(e, "me/2fa/recovery-codes")} className="grid max-w-sm gap-3">
                <p className="text-xs text-muted-foreground">{t("regenerateHint")}</p>
                <Field label={t("code")}><Input name="code" inputMode="numeric" autoComplete="one-time-code" maxLength={7} required /></Field>
                <Button type="submit" size="sm" disabled={pending} className="justify-self-start">{t("regenerate")}</Button>
              </form>
            )}
            {mode === "disable" && (
              <form onSubmit={(e) => submit(e, "me/2fa/disable")} className="grid max-w-sm gap-3">
                <p className="text-xs text-muted-foreground">{t("disableHint")}</p>
                <Field label={t("password")}><Input name="password" type="password" autoComplete="current-password" required /></Field>
                <Field label={t("code")}><Input name="code" inputMode="numeric" autoComplete="one-time-code" maxLength={7} required /></Field>
                <Button type="submit" size="sm" variant="destructive" disabled={pending} className="justify-self-start">{t("disable")}</Button>
              </form>
            )}
          </>
        )}
        {error && <FormMessage>{error}</FormMessage>}
      </div>
    </Panel>
  );
}

export function SessionsPanel({ sessions }: { sessions: SessionItem[] }) {
  const t = useTranslations("account.sessions");
  const format = useFormatter();
  const errorOf = useAccountError();
  const router = useRouter();
  const [error, setError] = useState<string | null>(null);
  const when = (value: string) => format.dateTime(new Date(value), { dateStyle: "medium", timeStyle: "short" });

  async function revoke(id: string) {
    setError(null);
    const { ok, data } = await postJson(api(`me/sessions/${id}/revoke`), {});
    if (ok) router.refresh();
    else setError(errorOf(data.code));
  }

  return (
    <Panel title={t("title")} description={t("description")} bodyClassName="p-0">
      <ul className="divide-y">
        {sessions.map((s) => (
          <li key={s.id} className="flex flex-wrap items-center gap-3 px-5 py-3">
            <Laptop className="size-5 shrink-0 text-muted-foreground" />
            <div className="min-w-0 flex-1">
              <p className="flex flex-wrap items-center gap-2 text-sm font-medium">
                <span className="truncate" title={s.userAgent ?? undefined}>{device(s.userAgent) ?? t("unknownDevice")}</span>
                <StatusBadge tone="muted">{t(`realm_${s.audience}`)}</StatusBadge>
                {s.current && <StatusBadge tone="primary">{t("current")}</StatusBadge>}
              </p>
              <p className="text-xs text-muted-foreground">
                {t("detail", { signedIn: when(s.createdAt), lastSeen: when(s.lastSeenAt), ip: s.ipAddress ?? "—" })}
              </p>
            </div>
            {!s.current && (
              <Button type="button" size="sm" variant="ghost" onClick={() => void revoke(s.id)}>{t("revoke")}</Button>
            )}
          </li>
        ))}
      </ul>
      {error && <div className="px-5 pb-4"><FormMessage>{error}</FormMessage></div>}
    </Panel>
  );
}
