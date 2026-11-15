"use client";

import { useState, type FormEvent } from "react";
import { useTranslations } from "next-intl";
import { AnimatePresence, motion } from "motion/react";
import { PartyPopper } from "lucide-react";
import { Button } from "@konnyaku/ui/components/animate-ui/components/buttons/button";
import { Input } from "@konnyaku/ui/components/ui/input";
import { Textarea } from "@konnyaku/ui/components/ui/textarea";
import { FormMessage } from "@konnyaku/ui/kk/blocks";
import { CheckRow, Field, NativeSelect } from "@konnyaku/ui/kk/field";

const consentVersion = "2026-11";

export function RegisterForm({ languages, locale, defaultLanguage }: {
  languages: { code: string; label: string }[];
  locale: string;
  defaultLanguage?: string;
}) {
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

  return (
    <AnimatePresence mode="wait" initial={false}>
      {state === "sent" ? (
        <motion.div
          key="sent"
          role="status"
          initial={{ opacity: 0, scale: 0.96 }}
          animate={{ opacity: 1, scale: 1 }}
          className="flex flex-col items-center gap-3 py-10 text-center"
        >
          <span className="grid size-14 place-items-center rounded-full bg-success/12 text-success">
            <PartyPopper className="size-7" />
          </span>
          <p className="text-lg font-semibold">{t("sentTitle")}</p>
          <p className="max-w-sm text-muted-foreground">{t("sent")}</p>
        </motion.div>
      ) : (
        <motion.form key="form" method="post" onSubmit={onSubmit} exit={{ opacity: 0 }} className="grid gap-4 sm:grid-cols-2">
          <Field label={t("studentName")} className="sm:col-span-2">
            <Input name="studentName" required autoComplete="name" />
          </Field>
          <Field label={t("birthYear")}>
            <Input name="birthYear" type="number" inputMode="numeric" min={1900} max={2100} />
          </Field>
          <Field label={t("language")}>
            <NativeSelect name="languageCode" required defaultValue={defaultLanguage ?? ""}>
              <option value="" disabled>{t("languagePlaceholder")}</option>
              {languages.map((l) => (
                <option key={l.code} value={l.code}>{l.label}</option>
              ))}
            </NativeSelect>
          </Field>
          <Field label={t("phone")}>
            <Input name="phone" type="tel" autoComplete="tel" />
          </Field>
          <Field label={t("email")}>
            <Input name="email" type="email" autoComplete="email" />
          </Field>
          <Field label={t("guardianName")} className="sm:col-span-2">
            <Input name="guardianName" />
          </Field>
          <Field label={t("goal")} className="sm:col-span-2">
            <Textarea name="goal" rows={2} />
          </Field>
          <Field label={t("schedule")} className="sm:col-span-2">
            <Input name="preferredSchedule" />
          </Field>
          <input name="website" tabIndex={-1} autoComplete="off" aria-hidden="true" className="absolute -left-[10000px]" />
          <CheckRow name="consent" required className="items-start text-muted-foreground sm:col-span-2">{t("consent")}</CheckRow>
          {error && <div className="sm:col-span-2"><FormMessage>{error}</FormMessage></div>}
          <Button type="submit" size="lg" disabled={state === "sending"} className="sm:col-span-2">
            {t(state === "sending" ? "sending" : "submit")}
          </Button>
        </motion.form>
      )}
    </AnimatePresence>
  );
}
