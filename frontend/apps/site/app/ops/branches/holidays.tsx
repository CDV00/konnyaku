import { getTranslations } from "next-intl/server";
import { CalendarOff } from "lucide-react";
import { Input } from "@konnyaku/ui/components/ui/input";
import { EmptyState, Panel } from "@konnyaku/ui/kk/blocks";
import { Field } from "@konnyaku/ui/kk/field";
import { ActionButton, ActionForm, FormSheet } from "../action-form";
import { ChangeHistory } from "../history";
import { localized, type Holiday } from "../lib";

/** Days off of one branch (or of the whole center when `branchId` is null), editable when `canEdit`. */
export async function HolidaysPanel({ holidays, branchId, canEdit, locale, names }: {
  holidays: Holiday[];
  branchId: string | null;
  canEdit: boolean;
  locale: string;
  names: Record<string, string>;
}) {
  const t = await getTranslations("admin");
  const fields = (holiday?: Holiday) => (
    <>
      {branchId && <input type="hidden" name="branchId" value={branchId} />}
      <Field label={t("holidays.date")}><Input name="date" type="date" required defaultValue={holiday?.date} /></Field>
      <Field label={t("fields.nameVi")}><Input name="name.vi" required defaultValue={holiday?.name.vi} /></Field>
      <Field label={t("fields.nameEn")}><Input name="name.en" defaultValue={holiday?.name.en} /></Field>
    </>
  );
  return (
    <Panel
      title={branchId ? t("holidays.branchTitle") : t("holidays.centerTitle")}
      description={t("holidays.hint")}
      action={
        canEdit && (
          <FormSheet triggerLabel={t("holidays.create")} title={t("holidays.create")} variant="outline" size="sm">
            <ActionForm api="organization/holidays" submitLabel={t("save")}>{fields()}</ActionForm>
          </FormSheet>
        )
      }
      bodyClassName="p-0"
    >
      {holidays.length === 0 ? (
        <div className="p-5"><EmptyState icon={CalendarOff} title={t("holidays.empty")} /></div>
      ) : (
        <ul className="divide-y">
          {holidays.map((h) => (
            <li key={h.id} className="flex flex-wrap items-center justify-between gap-3 px-5 py-3">
              <span>
                <span className="font-medium tabular-nums">{h.date}</span>
                <span className="ml-3">{localized(h.name, locale)}</span>
              </span>
              {canEdit && (
                <span className="flex flex-wrap items-start gap-2">
                  <FormSheet triggerLabel={t("edit")} title={t("holidays.edit")} variant="ghost" size="sm" icon="edit">
                    <ActionForm api={`organization/holidays/${h.id}`} method="PUT" submitLabel={t("save")}>{fields(h)}</ActionForm>
                    <section className="mt-6 space-y-3">
                      <h3 className="text-sm font-semibold">{t("history.title")}</h3>
                      <ChangeHistory sources={[{ module: "organization", id: h.id }]} names={names} />
                    </section>
                  </FormSheet>
                  <ActionButton api={`organization/holidays/${h.id}`} method="DELETE" label={t("delete")} variant="ghost" confirm={t("holidays.deleteConfirm")} />
                </span>
              )}
            </li>
          ))}
        </ul>
      )}
    </Panel>
  );
}
