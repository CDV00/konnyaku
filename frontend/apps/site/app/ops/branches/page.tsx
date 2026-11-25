import Link from "next/link";
import { getLocale, getTranslations } from "next-intl/server";
import { Building2, ChevronRight, Globe2, MapPin } from "lucide-react";
import { requireMe } from "@konnyaku/session";
import { Input } from "@konnyaku/ui/components/ui/input";
import { EmptyState, PageHeader, StatusBadge } from "@konnyaku/ui/kk/blocks";
import { CheckRow, Field } from "@konnyaku/ui/kk/field";
import { ActionButton, ActionForm, FormSheet } from "../action-form";
import { ChangeHistory } from "../history";
import { getAccess, getJson, holidayWindow, localized, type Branch, type Holiday } from "../lib";
import { HolidaysPanel } from "./holidays";

export default async function BranchesPage() {
  const { token } = await requireMe("staff");
  const t = await getTranslations("admin");
  const locale = await getLocale();
  const { from, to } = holidayWindow();
  const [branches, holidays, access] = await Promise.all([
    getJson<Branch[]>("/api/organization/branches", token, []),
    getJson<Holiday[]>(`/api/organization/holidays?from=${from}&to=${to}`, token, []),
    getAccess(token),
  ]);
  const canManage = access.can("organization.branches");
  const names = Object.fromEntries(branches.map((b) => [b.id, localized(b.name, locale)]));
  const fields = (branch?: Branch) => (
    <>
      <Field label={t("fields.code")}><Input name="code" required defaultValue={branch?.code} /></Field>
      <Field label={t("fields.nameVi")}><Input name="name.vi" required defaultValue={branch?.name.vi} /></Field>
      <Field label={t("fields.nameEn")}><Input name="name.en" defaultValue={branch?.name.en} /></Field>
      <Field label={t("fields.timeZone")} hint={branch ? t("branches.timeZoneEditHint") : t("branches.timeZoneHint")}>
        <Input name="timeZone" required defaultValue={branch?.timeZone ?? "Asia/Ho_Chi_Minh"} />
      </Field>
      <CheckRow name="isVirtual" defaultChecked={branch?.isVirtual}>{t("branches.virtual")}</CheckRow>
    </>
  );

  return (
    <div className="space-y-6">
      <PageHeader
        title={t("branches.title")}
        description={t("branches.description")}
        actions={
          canManage && (
            <FormSheet triggerLabel={t("branches.create")} title={t("branches.create")}>
              <ActionForm api="organization/branches" submitLabel={t("save")}>{fields()}</ActionForm>
            </FormSheet>
          )
        }
      />
      {branches.length === 0 ? (
        <EmptyState icon={Building2} title={t("branches.empty")} />
      ) : (
        <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
          {branches.map((b) => (
            <article key={b.id} className={`flex flex-col rounded-xl border bg-card p-5 shadow-xs ${b.isActive ? "" : "opacity-70"}`}>
              <div className="flex items-start justify-between gap-3">
                <span className="grid size-10 place-items-center rounded-lg bg-primary/10 text-primary">
                  {b.isVirtual ? <Globe2 className="size-5" /> : <MapPin className="size-5" />}
                </span>
                <span className="flex items-center gap-2">
                  {!b.isActive && <StatusBadge tone="muted">{t("inactive")}</StatusBadge>}
                  <span className="rounded bg-muted px-2 py-0.5 font-mono text-xs">{b.code}</span>
                </span>
              </div>
              <h2 className="mt-4 font-semibold">{localized(b.name, locale)}</h2>
              <p className="mt-1 text-sm text-muted-foreground">
                {b.isVirtual ? t("branches.virtual") : t("branches.physical")}
                {" · "}
                {b.timeZone}
              </p>
              <div className="mt-4 flex flex-wrap items-start gap-2 border-t pt-4">
                <Link href={`/ops/branches/${b.id}`} className="inline-flex h-8 items-center gap-1 rounded-md px-2 text-sm font-medium text-primary hover:bg-accent">
                  {t("branches.open")}
                  <ChevronRight className="size-4" />
                </Link>
                {canManage && (
                  <>
                    <FormSheet triggerLabel={t("edit")} title={t("branches.edit", { code: b.code })} variant="ghost" size="sm" icon="edit">
                      <ActionForm api={`organization/branches/${b.id}`} method="PUT" submitLabel={t("save")}>{fields(b)}</ActionForm>
                      <section className="mt-6 space-y-3">
                        <h3 className="text-sm font-semibold">{t("history.title")}</h3>
                        <ChangeHistory sources={[{ module: "organization", id: b.id }]} names={names} />
                      </section>
                    </FormSheet>
                    {b.isActive ? (
                      <ActionButton api={`organization/branches/${b.id}/deactivate`} label={t("deactivate")} variant="ghost" confirm={t("branches.deactivateConfirm")} />
                    ) : (
                      <ActionButton api={`organization/branches/${b.id}/activate`} label={t("activate")} variant="ghost" />
                    )}
                  </>
                )}
              </div>
            </article>
          ))}
        </div>
      )}
      <HolidaysPanel
        holidays={holidays.filter((h) => h.branchId === null)}
        branchId={null}
        canEdit={access.can("organization.holidays")}
        locale={locale}
        names={names}
      />
    </div>
  );
}
