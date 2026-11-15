import { getLocale, getTranslations } from "next-intl/server";
import { Building2, Globe2, MapPin } from "lucide-react";
import { requireMe } from "@konnyaku/session";
import { Input } from "@konnyaku/ui/components/ui/input";
import { EmptyState, PageHeader } from "@konnyaku/ui/kk/blocks";
import { CheckRow, Field } from "@konnyaku/ui/kk/field";
import { ActionForm, FormSheet } from "../action-form";
import { getJson, localized, type Branch } from "../lib";

export default async function BranchesPage() {
  const { token } = await requireMe("staff");
  const t = await getTranslations("admin");
  const locale = await getLocale();
  const branches = await getJson<Branch[]>("/api/organization/branches", token, []);
  return (
    <div className="space-y-6">
      <PageHeader
        title={t("branches.title")}
        description={t("branches.description")}
        actions={
          <FormSheet triggerLabel={t("branches.create")} title={t("branches.create")}>
            <ActionForm api="organization/branches" submitLabel={t("save")}>
              <Field label={t("fields.code")}><Input name="code" required /></Field>
              <Field label={t("fields.nameVi")}><Input name="name.vi" required /></Field>
              <Field label={t("fields.nameEn")}><Input name="name.en" /></Field>
              <Field label={t("fields.timeZone")} hint={t("branches.timeZoneHint")}>
                <Input name="timeZone" required defaultValue="Asia/Ho_Chi_Minh" />
              </Field>
              <CheckRow name="isVirtual">{t("branches.virtual")}</CheckRow>
            </ActionForm>
          </FormSheet>
        }
      />
      {branches.length === 0 ? (
        <EmptyState icon={Building2} title={t("branches.empty")} />
      ) : (
        <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
          {branches.map((b) => (
            <article key={b.id} className="rounded-xl border bg-card p-5 shadow-xs">
              <div className="flex items-start justify-between gap-3">
                <span className="grid size-10 place-items-center rounded-lg bg-primary/10 text-primary">
                  {b.isVirtual ? <Globe2 className="size-5" /> : <MapPin className="size-5" />}
                </span>
                <span className="rounded bg-muted px-2 py-0.5 font-mono text-xs">{b.code}</span>
              </div>
              <h2 className="mt-4 font-semibold">{localized(b.name, locale)}</h2>
              <p className="mt-1 text-sm text-muted-foreground">
                {b.isVirtual ? t("branches.virtual") : t("branches.physical")}
                {" · "}
                {b.timeZone}
              </p>
            </article>
          ))}
        </div>
      )}
    </div>
  );
}
