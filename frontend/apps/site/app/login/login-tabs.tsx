"use client";

import { useState, type FormEvent } from "react";
import { useTranslations } from "next-intl";
import { Briefcase, GraduationCap, KeyRound, LogIn } from "lucide-react";
import { Button } from "@konnyaku/ui/components/animate-ui/components/buttons/button";
import { Tabs, TabsContent, TabsContents, TabsList, TabsTrigger } from "@konnyaku/ui/components/animate-ui/components/radix/tabs";
import { Input } from "@konnyaku/ui/components/ui/input";
import { FormMessage } from "@konnyaku/ui/kk/blocks";
import { Field } from "@konnyaku/ui/kk/field";

type Realm = "learn" | "staff";

function RealmForm({ realm }: { realm: Realm }) {
  const t = useTranslations("login");
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
    const body = (await response.json().catch(() => ({}))) as { redirect?: string; code?: string };
    if (response.ok && body.redirect) {
      // Full navigation: the target area (learn, teach, ops) has its own root layout.
      window.location.assign(body.redirect);
      return;
    }
    const key = `errors.${(body.code ?? "unknown").replace(".", "_")}`;
    setError(t.has(key) ? t(key) : t("errors.unknown"));
    setSubmitting(false);
  }

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
    </form>
  );
}

/** Two separate sign-in realms (separate cookies), so a parent and a teacher on one device never collide. */
export function LoginTabs({ initialRealm }: { initialRealm: Realm }) {
  const t = useTranslations("login");
  return (
    <Tabs defaultValue={initialRealm} className="w-full">
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
      <p className="flex items-center gap-2 pt-2 text-xs text-muted-foreground">
        <KeyRound className="size-3.5" />
        {t("forgot")}
      </p>
    </Tabs>
  );
}
