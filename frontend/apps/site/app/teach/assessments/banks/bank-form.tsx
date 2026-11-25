"use client";

import { useState, type FormEvent } from "react";
import { useRouter } from "next/navigation";
import { useLocale, useTranslations } from "next-intl";
import { Pencil, Plus } from "lucide-react";
import { Button } from "@konnyaku/ui/components/animate-ui/components/buttons/button";
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle, SheetTrigger } from "@konnyaku/ui/components/animate-ui/components/radix/sheet";
import { Input } from "@konnyaku/ui/components/ui/input";
import { Textarea } from "@konnyaku/ui/components/ui/textarea";
import { FormMessage } from "@konnyaku/ui/kk/blocks";
import { Field, NativeSelect } from "@konnyaku/ui/kk/field";
import { callApi } from "@/assessment/client";
import type { Bank } from "@/assessment/types";
import { localizedName, useErrorText, type Language } from "../authoring";

/** Create a bank (then open it) or edit its settings, in a side sheet. */
export function BankFormSheet({ languages, bank }: { languages: Language[]; bank?: Bank }) {
  const t = useTranslations("assessmentAuthoring");
  const locale = useLocale();
  const router = useRouter();
  const errorText = useErrorText();
  const [open, setOpen] = useState(false);
  const [pending, setPending] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function onSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = new FormData(event.currentTarget);
    const body = {
      name: String(form.get("name") ?? ""),
      languageCode: String(form.get("languageCode") ?? ""),
      programId: bank?.programId ?? null,
      visibility: String(form.get("visibility") ?? "Private"),
      description: String(form.get("description") ?? "") || null,
    };
    setPending(true);
    setError(null);
    const result = bank
      ? await callApi<Bank>(`/teach/api/assessment/banks/${bank.id}`, "PUT", body)
      : await callApi<Bank>("/teach/api/assessment/banks", "POST", body);
    setPending(false);
    if (!result.ok) {
      setError(errorText(result.code, result.params));
      return;
    }
    setOpen(false);
    if (bank) router.refresh();
    else router.push(`/teach/assessments/banks/${result.data.id}`);
  }

  return (
    <Sheet open={open} onOpenChange={setOpen}>
      <SheetTrigger asChild>
        <Button variant={bank ? "outline" : "default"}>
          {bank ? <Pencil /> : <Plus />}
          {bank ? t("banks.edit") : t("banks.new")}
        </Button>
      </SheetTrigger>
      <SheetContent className="w-full overflow-y-auto sm:w-[28rem] sm:max-w-md" closeLabel={t("common.close")}>
        <SheetHeader className="border-b">
          <SheetTitle>{bank ? t("banks.edit") : t("banks.new")}</SheetTitle>
          <SheetDescription>{t("banks.formHint")}</SheetDescription>
        </SheetHeader>
        <form onSubmit={onSubmit} className="space-y-4 px-4 pb-6">
          <Field label={t("banks.name")}>
            <Input name="name" required maxLength={200} defaultValue={bank?.name} />
          </Field>
          <Field label={t("common.language")}>
            <NativeSelect name="languageCode" required defaultValue={bank?.languageCode ?? languages[0]?.code}>
              {languages.map((l) => (
                <option key={l.code} value={l.code}>
                  {localizedName(l.name, locale) || l.code}
                </option>
              ))}
            </NativeSelect>
          </Field>
          <Field label={t("banks.visibility")} hint={t("banks.visibilityHint")}>
            <NativeSelect name="visibility" defaultValue={bank?.visibility ?? "Private"}>
              <option value="Private">{t("banks.visibilities.Private")}</option>
              <option value="Shared">{t("banks.visibilities.Shared")}</option>
            </NativeSelect>
          </Field>
          <Field label={t("common.description")}>
            <Textarea name="description" rows={3} maxLength={2000} defaultValue={bank?.description ?? ""} />
          </Field>
          {error && <FormMessage>{error}</FormMessage>}
          <Button type="submit" disabled={pending}>
            {t("common.save")}
          </Button>
        </form>
      </SheetContent>
    </Sheet>
  );
}
