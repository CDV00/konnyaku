"use client";

import Link from "next/link";
import { useState, type FormEvent } from "react";
import { useTranslations } from "next-intl";
import { Briefcase, GraduationCap, KeyRound, LogIn, ShieldCheck } from "lucide-react";
import { Button } from "@konnyaku/ui/components/animate-ui/components/buttons/button";
import { Tabs, TabsContent, TabsContents, TabsList, TabsTrigger } from "@konnyaku/ui/components/animate-ui/components/radix/tabs";
import { Input } from "@konnyaku/ui/components/ui/input";
import { FormMessage } from "@konnyaku/ui/kk/blocks";
import { Field } from "@konnyaku/ui/kk/field";
import { RecoveryCodes, TwoFactorEnrollment, postJson } from "../_account/two-factor";

type Realm = "learn" | "staff";

/** After the password: a code from the authenticator (verify), or setting 2FA up first (enroll). */
type Step = { kind: "password" } | { kind: "verify"; challenge: string } | { kind: "enroll"; challenge: string } | { kind: "codes"; codes: string[]; redirect: string };

function useLoginError() {
  const t = useTranslations("login");
  return (code: string | undefined) => {
    const key = `errors.${(code ?? "unknown").replaceAll(".", "_")}`;
    return t.has(key) ? t(key) : t("errors.unknown");
  };
}

/** Full navigation: the target area (learn, teach, ops) has its own root layout. */
function go(redirect: string) {
  window.location.assign(redirect);
}

function VerifyStep({ challenge, onRestart }: { challenge: string; onRestart: () => void }) {
  const t = useTranslations("login.mfa");
  const errorOf = useLoginError();
  const [recovery, setRecovery] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState(false);

  async function onSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setPending(true);
    setError(null);
    const value = String(new FormData(event.currentTarget).get("value") ?? "");
    const { ok, data } = await postJson<{ redirect?: string }>("/api/mfa/verify", recovery ? { challenge, recoveryCode: value } : { challenge, code: value });
    if (ok && data.redirect) return go(data.redirect);
    setPending(false);
    if (data.code === "identity.sign_in_expired") onRestart();
    setError(errorOf(data.code));
  }

  return (
    <form onSubmit={onSubmit} className="grid gap-4 pt-2">
      <p className="flex items-start gap-2 text-sm text-muted-foreground">
        <ShieldCheck className="mt-0.5 size-4 shrink-0 text-primary" />
        {t(recovery ? "recoveryHint" : "verifyHint")}
      </p>
      <Field label={t(recovery ? "recoveryCode" : "code")}>
        {recovery ? (
          <Input key="recovery" name="value" autoComplete="off" required autoFocus />
        ) : (
          <Input key="code" name="value" inputMode="numeric" autoComplete="one-time-code" maxLength={7} required autoFocus />
        )}
      </Field>
      {error && <FormMessage>{error}</FormMessage>}
      <Button type="submit" size="lg" disabled={pending} className="w-full">{t("submit")}</Button>
      <button type="button" className="text-sm text-primary underline-offset-4 hover:underline" onClick={() => setRecovery(!recovery)}>
        {t(recovery ? "useCode" : "useRecovery")}
      </button>
    </form>
  );
}

function StaffSteps({ step, setStep }: { step: Step; setStep: (step: Step) => void }) {
  const t = useTranslations("login.mfa");
  const restart = () => setStep({ kind: "password" });
  if (step.kind === "verify") return <VerifyStep challenge={step.challenge} onRestart={restart} />;
  if (step.kind === "enroll") {
    return (
      <div className="grid gap-4 pt-2">
        <p className="text-sm font-medium">{t("enrollTitle")}</p>
        <p className="text-sm text-muted-foreground">{t("enrollHint")}</p>
        <TwoFactorEnrollment
          setupUrl="/api/mfa/setup"
          setupBody={{ challenge: step.challenge }}
          enableUrl="/api/mfa/enable"
          enableBody={{ challenge: step.challenge }}
          onEnabled={(data) => setStep({ kind: "codes", codes: data.recoveryCodes, redirect: data.redirect ?? "/ops" })}
        />
      </div>
    );
  }
  if (step.kind === "codes") {
    return (
      <div className="grid gap-4 pt-2">
        <RecoveryCodes codes={step.codes} />
        <Button type="button" size="lg" onClick={() => go(step.redirect)}>{t("continue")}</Button>
      </div>
    );
  }
  return null;
}

function RealmForm({ realm }: { realm: Realm }) {
  const t = useTranslations("login");
  const errorOf = useLoginError();
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [step, setStep] = useState<Step>({ kind: "password" });

  async function onSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setSubmitting(true);
    setError(null);
    const form = new FormData(event.currentTarget);
    const { ok, data } = await postJson<{ redirect?: string; challenge?: string; nextStep?: "verify" | "enroll" }>(
      `/api/session/${realm}`,
      Object.fromEntries(form),
    );
    if (ok && data.redirect) return go(data.redirect);
    setSubmitting(false);
    if (ok && data.challenge && data.nextStep) {
      setStep({ kind: data.nextStep, challenge: data.challenge });
      return;
    }
    setError(errorOf(data.code));
  }

  if (step.kind !== "password") return <StaffSteps step={step} setStep={setStep} />;

  const learn = realm === "learn";
  return (
    <form method="post" onSubmit={onSubmit} className="grid gap-4 pt-2">
      <p className="text-sm text-muted-foreground">{t(learn ? "learnHint" : "staffHint")}</p>
      <Field label={t(learn ? "username" : "email")}>
        <Input name={learn ? "username" : "email"} type={learn ? "text" : "email"} autoComplete="username" required autoFocus={learn} />
      </Field>
      <Field label={t("password")}>
        <Input name="password" type="password" autoComplete="current-password" required />
      </Field>
      {error && <FormMessage>{error}</FormMessage>}
      <Button type="submit" size="lg" disabled={submitting} className="w-full">
        <LogIn />
        {t(submitting ? "submitting" : "submit")}
      </Button>
      <p className="flex items-center gap-2 text-xs text-muted-foreground">
        <KeyRound className="size-3.5 shrink-0" />
        {learn ? (
          t("forgotLearn")
        ) : (
          <Link href="/login/forgot" className="text-primary underline-offset-4 hover:underline">{t("forgotStaff")}</Link>
        )}
      </p>
    </form>
  );
}

/** Two separate sign-in realms (separate cookies), so a parent and a teacher on one device never collide. */
export function LoginTabs({ initialRealm, ended }: { initialRealm: Realm; ended?: boolean }) {
  const t = useTranslations("login");
  return (
    <Tabs defaultValue={initialRealm} className="w-full">
      {ended && <FormMessage tone="success">{t("ended")}</FormMessage>}
      <TabsList className="h-11 w-full">
        <TabsTrigger value="learn" className="gap-2">
          <GraduationCap />
          {t("learnTab")}
        </TabsTrigger>
        <TabsTrigger value="staff" className="gap-2">
          <Briefcase />
          {t("staffTab")}
        </TabsTrigger>
      </TabsList>
      <TabsContents>
        <TabsContent value="learn">
          <RealmForm realm="learn" />
        </TabsContent>
        <TabsContent value="staff">
          <RealmForm realm="staff" />
        </TabsContent>
      </TabsContents>
    </Tabs>
  );
}
