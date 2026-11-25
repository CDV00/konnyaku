"use client";

import { useState, type FormEvent } from "react";
import { useTranslations } from "next-intl";
import { ArrowRight, Copy, KeyRound, LogIn, RotateCcw, ShieldCheck } from "lucide-react";
import { Button } from "@konnyaku/ui/components/animate-ui/components/buttons/button";
import { Input } from "@konnyaku/ui/components/ui/input";
import { FormMessage } from "@konnyaku/ui/kk/blocks";
import { Field } from "@konnyaku/ui/kk/field";

type Enrollment = { secret: string; otpAuthUri: string; qr: string };

type Stage =
  | { kind: "password" }
  | { kind: "code"; enrollment: Enrollment | null }
  | { kind: "recovery"; codes: string[]; redirect: string };

/** Groups a base32 key in fours so it can be typed into an authenticator app. */
function grouped(secret: string): string {
  return secret.match(/.{1,4}/g)?.join(" ") ?? secret;
}

/**
 * Operator sign-in: email + password, then a TOTP code. The first time, the account enrolls an authenticator app
 * (QR code + manual key) and receives single-use recovery codes, shown once.
 */
export function PlatformLoginForm() {
  const t = useTranslations("platform");
  const [stage, setStage] = useState<Stage>({ kind: "password" });
  const [useRecovery, setUseRecovery] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  function showError(code: string | undefined) {
    const key = `errors.${(code ?? "unknown").replaceAll(".", "_")}`;
    setError(t.has(key) ? t(key as "errors.unknown", { code: code ?? "unknown" }) : t("errors.unknown", { code: code ?? "unknown" }));
  }

  async function post(step: "login" | "verify", body: unknown) {
    const response = await fetch(`/platform/api/session/${step}`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    });
    return { ok: response.ok, body: (await response.json().catch(() => ({}))) as Record<string, unknown> };
  }

  async function onPassword(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setSubmitting(true);
    setError(null);
    const form = new FormData(event.currentTarget);
    const { ok, body } = await post("login", { email: form.get("email"), password: form.get("password") });
    setSubmitting(false);
    if (!ok) return showError(body.code as string | undefined);
    const enrollment = body.enrollmentRequired
      ? { secret: String(body.secret), otpAuthUri: String(body.otpAuthUri), qr: String(body.qr) }
      : null;
    setStage({ kind: "code", enrollment });
  }

  async function onCode(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setSubmitting(true);
    setError(null);
    const form = new FormData(event.currentTarget);
    const { ok, body } = await post("verify", useRecovery ? { recoveryCode: form.get("recoveryCode") } : { code: form.get("code") });
    if (!ok) {
      setSubmitting(false);
      const code = body.code as string | undefined;
      if (code === "platform.challenge_expired") setStage({ kind: "password" });
      return showError(code);
    }
    const redirect = String(body.redirect ?? "/platform");
    const codes = body.recoveryCodes as string[] | null;
    if (codes && codes.length > 0) {
      setSubmitting(false);
      setStage({ kind: "recovery", codes, redirect });
      return;
    }
    // Full navigation: the console has its own root layout.
    window.location.assign(redirect);
  }

  function restart() {
    setError(null);
    setUseRecovery(false);
    setStage({ kind: "password" });
  }

  if (stage.kind === "recovery") {
    return (
      <div className="grid gap-4">
        <div className="space-y-1">
          <h2 className="flex items-center gap-2 font-semibold"><KeyRound className="size-4 text-primary" />{t("login.recoveryTitle")}</h2>
          <p className="text-sm text-muted-foreground">{t("login.recoveryBody")}</p>
        </div>
        <ul className="grid grid-cols-2 gap-2 rounded-lg border bg-muted/40 p-3 font-mono text-sm">
          {stage.codes.map((code) => <li key={code} className="text-center">{code}</li>)}
        </ul>
        <Button type="button" variant="outline" onClick={() => navigator.clipboard?.writeText(stage.codes.join("\n"))}>
          <Copy />
          {t("copy")}
        </Button>
        <Button type="button" size="lg" onClick={() => window.location.assign(stage.redirect)}>
          {t("login.recoveryDone")}
          <ArrowRight />
        </Button>
      </div>
    );
  }

  if (stage.kind === "code") {
    const enrollment = stage.enrollment;
    return (
      <form method="post" onSubmit={onCode} className="grid gap-4">
        <div className="space-y-1">
          <h2 className="flex items-center gap-2 font-semibold">
            <ShieldCheck className="size-4 text-primary" />
            {t(enrollment ? "login.enrollTitle" : "login.codeTitle")}
          </h2>
          <p className="text-sm text-muted-foreground">{t(enrollment ? "login.enrollHint" : "login.codeHint")}</p>
        </div>
        {enrollment && (
          <div className="grid justify-items-center gap-3 rounded-lg border p-4">
            {/* eslint-disable-next-line @next/next/no-img-element -- inline SVG data generated on the server */}
            <img
              src={`data:image/svg+xml;utf8,${encodeURIComponent(enrollment.qr)}`}
              alt={t("login.qrAlt")}
              className="size-44 rounded-md bg-white p-1"
            />
            <div className="grid w-full gap-1 text-center text-xs text-muted-foreground">
              <span>{t("login.enrollSecret")}</span>
              <code className="rounded bg-muted px-2 py-1 font-mono text-sm font-semibold break-all text-foreground">{grouped(enrollment.secret)}</code>
              <a href={enrollment.otpAuthUri} className="text-primary underline-offset-4 hover:underline">{t("login.enrollOpen")}</a>
            </div>
          </div>
        )}
        {useRecovery ? (
          <Field label={t("login.recoveryCode")} hint={t("login.recoveryHint")}>
            <Input key="recovery" name="recoveryCode" autoComplete="off" required autoFocus spellCheck={false} />
          </Field>
        ) : (
          <Field label={t("login.code")}>
            <Input
              key="code"
              name="code"
              inputMode="numeric"
              autoComplete="one-time-code"
              pattern="[0-9 ]{6,7}"
              maxLength={7}
              required
              autoFocus
              className="font-mono tracking-[0.3em]"
            />
          </Field>
        )}
        {error && <FormMessage>{error}</FormMessage>}
        <Button type="submit" size="lg" disabled={submitting} className="w-full">
          <ShieldCheck />
          {t(submitting ? "login.submitting" : "login.verify")}
        </Button>
        <div className="flex flex-wrap justify-between gap-2 text-sm">
          {!enrollment && (
            <button type="button" className="text-primary underline-offset-4 hover:underline" onClick={() => { setError(null); setUseRecovery(!useRecovery); }}>
              {t(useRecovery ? "login.useCode" : "login.useRecovery")}
            </button>
          )}
          <button type="button" className="inline-flex items-center gap-1 text-muted-foreground hover:text-foreground" onClick={restart}>
            <RotateCcw className="size-3.5" />
            {t("login.restart")}
          </button>
        </div>
      </form>
    );
  }

  return (
    <form method="post" onSubmit={onPassword} className="grid gap-4">
      <Field label={t("login.email")}>
        <Input name="email" type="email" autoComplete="username" required autoFocus />
      </Field>
      <Field label={t("login.password")}>
        <Input name="password" type="password" autoComplete="current-password" required />
      </Field>
      {error && <FormMessage>{error}</FormMessage>}
      <Button type="submit" size="lg" disabled={submitting} className="w-full">
        <LogIn />
        {t(submitting ? "login.submitting" : "login.continue")}
      </Button>
    </form>
  );
}
