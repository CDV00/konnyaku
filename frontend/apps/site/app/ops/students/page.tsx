import { getLocale, getTranslations } from "next-intl/server";
import { GraduationCap, KeyRound } from "lucide-react";
import { requireMe } from "@konnyaku/session";
import { Input } from "@konnyaku/ui/components/ui/input";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@konnyaku/ui/components/ui/table";
import { EmptyState, PageHeader, Panel, StatusBadge } from "@konnyaku/ui/kk/blocks";
import { CheckRow, Field, NativeSelect } from "@konnyaku/ui/kk/field";
import { ActionButton, ActionForm, FormSheet } from "../action-form";
import { ChangeHistory } from "../history";
import { getAccess, getJson, localized, type Branch, type Student } from "../lib";

/** Contact details come only to members who keep student records (consultants, admins). */
type StudentDetail = Student & { guardianName: string | null; phone: string | null; email: string | null };

const locales = ["vi", "en"] as const;

export default async function StudentsPage() {
  const { token } = await requireMe("staff");
  const t = await getTranslations("admin");
  const locale = await getLocale();
  const access = await getAccess(token);
  // Student records are center-wide: any grant of the permission (a branch manager's too) covers them.
  const canEdit = access.any("crm.students");
  const [students, branches] = await Promise.all([
    canEdit
      ? getJson<StudentDetail[]>("/api/crm/students/details", token, [])
      : getJson<Student[]>("/api/crm/students", token, []).then((list) => list.map((s) => ({ ...s, guardianName: null, phone: null, email: null }))),
    getJson<Branch[]>("/api/organization/branches", token, []),
  ]);
  const branchName = new Map(branches.map((b) => [b.id, localized(b.name, locale)]));
  const names = Object.fromEntries(branchName.entries());
  const branchSelect = (current?: string | null) => (
    <Field label={t("classes.branch")}>
      <NativeSelect name="homeBranchId" defaultValue={current ?? ""}>
        <option value="">{t("students.noBranch")}</option>
        {branches.filter((b) => b.isActive || b.id === current).map((b) => <option key={b.id} value={b.id}>{localized(b.name, locale)}</option>)}
      </NativeSelect>
    </Field>
  );
  const contactFields = (s?: StudentDetail) => (
    <>
      <div className="grid grid-cols-2 gap-3">
        <Field label={t("students.birthYear")}><Input name="birthYear" type="number" min={1900} max={2100} defaultValue={s?.birthYear ?? undefined} /></Field>
        <Field label={t("fields.phone")}><Input name="phone" type="tel" defaultValue={s?.phone ?? undefined} /></Field>
      </div>
      <Field label={t("students.guardian")}><Input name="guardianName" defaultValue={s?.guardianName ?? undefined} /></Field>
      <Field label={t("fields.email")}><Input name="email" type="email" defaultValue={s?.email ?? undefined} /></Field>
    </>
  );

  return (
    <div className="space-y-6">
      <PageHeader
        title={t("students.title")}
        description={t("students.description")}
        actions={
          canEdit && (
            <FormSheet triggerLabel={t("students.create")} title={t("students.create")} description={t("students.createHint")}>
              <ActionForm endpoint="/ops/api/students" submitLabel={t("save")} showResult={["username", "temporaryPassword"]}>
                <Field label={t("students.fullName")}><Input name="fullName" required /></Field>
                {contactFields()}
                {branchSelect()}
                <CheckRow name="createAccount" defaultChecked>{t("students.createAccount")}</CheckRow>
              </ActionForm>
            </FormSheet>
          )
        }
      />
      <Panel bodyClassName="p-0">
        {students.length === 0 ? (
          <div className="p-5"><EmptyState icon={GraduationCap} title={t("students.empty")} /></div>
        ) : (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead className="pl-5">{t("students.fullName")}</TableHead>
                <TableHead>{t("students.birthYear")}</TableHead>
                <TableHead className="hidden md:table-cell">{t("classes.branch")}</TableHead>
                <TableHead className="hidden sm:table-cell">{t("students.account")}</TableHead>
                <TableHead className="pr-5" />
              </TableRow>
            </TableHeader>
            <TableBody>
              {students.map((s) => (
                <TableRow key={s.id} className={s.isActive ? "" : "opacity-70"}>
                  <TableCell className="pl-5">
                    <p className="flex flex-wrap items-center gap-2 font-medium">
                      {s.fullName}
                      {!s.isActive && <StatusBadge tone="muted">{t("inactive")}</StatusBadge>}
                    </p>
                    {s.phone && <p className="text-xs text-muted-foreground">{s.phone}</p>}
                  </TableCell>
                  <TableCell className="tabular-nums">{s.birthYear}</TableCell>
                  <TableCell className="hidden md:table-cell">
                    {s.homeBranchId ? branchName.get(s.homeBranchId) : <span className="text-muted-foreground">{t("students.noBranch")}</span>}
                  </TableCell>
                  <TableCell className="hidden sm:table-cell">
                    {s.userId ? (
                      <StatusBadge tone="success"><KeyRound className="size-3" />{t("yes")}</StatusBadge>
                    ) : (
                      <StatusBadge>{t("no")}</StatusBadge>
                    )}
                  </TableCell>
                  <TableCell className="pr-5 text-right">
                    {canEdit && (
                      <span className="inline-flex flex-wrap items-start justify-end gap-2">
                        <FormSheet triggerLabel={t("edit")} title={t("students.edit", { name: s.fullName })} variant="ghost" size="sm" icon="edit">
                          <ActionForm api={`crm/students/${s.id}`} method="PUT" submitLabel={t("save")}>
                            <Field label={t("students.fullName")}><Input name="fullName" required defaultValue={s.fullName} /></Field>
                            {contactFields(s)}
                            {branchSelect(s.homeBranchId)}
                            <Field label={t("students.locale")}>
                              <NativeSelect name="communicationLocale" defaultValue={s.communicationLocale}>
                                {locales.map((l) => <option key={l} value={l}>{t(`locales.${l}`)}</option>)}
                              </NativeSelect>
                            </Field>
                          </ActionForm>
                          <section className="mt-6 space-y-3">
                            <h3 className="text-sm font-semibold">{t("history.title")}</h3>
                            <ChangeHistory sources={[{ module: "crm", id: s.id }]} names={names} />
                          </section>
                        </FormSheet>
                        {s.isActive ? (
                          <ActionButton api={`crm/students/${s.id}/deactivate`} label={t("deactivate")} variant="ghost" confirm={t("students.deactivateConfirm")} />
                        ) : (
                          <ActionButton api={`crm/students/${s.id}/activate`} label={t("activate")} variant="ghost" />
                        )}
                      </span>
                    )}
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </Panel>
    </div>
  );
}
