import { getLocale, getTranslations } from "next-intl/server";
import { GraduationCap, KeyRound } from "lucide-react";
import { requireMe } from "@konnyaku/session";
import { Input } from "@konnyaku/ui/components/ui/input";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@konnyaku/ui/components/ui/table";
import { EmptyState, PageHeader, Panel, StatusBadge } from "@konnyaku/ui/kk/blocks";
import { CheckRow, Field, NativeSelect } from "@konnyaku/ui/kk/field";
import { ActionForm, FormSheet } from "../action-form";
import { getJson, localized, type Branch, type Student } from "../lib";

export default async function StudentsPage() {
  const { token } = await requireMe("staff");
  const t = await getTranslations("admin");
  const locale = await getLocale();
  const [students, branches] = await Promise.all([
    getJson<Student[]>("/api/crm/students", token, []),
    getJson<Branch[]>("/api/organization/branches", token, []),
  ]);
  const branchName = new Map(branches.map((b) => [b.id, localized(b.name, locale)]));

  return (
    <div className="space-y-6">
      <PageHeader
        title={t("students.title")}
        description={t("students.description")}
        actions={
          <FormSheet triggerLabel={t("students.create")} title={t("students.create")} description={t("students.createHint")}>
            <ActionForm endpoint="/ops/api/students" submitLabel={t("save")} showResult={["username", "temporaryPassword"]}>
              <Field label={t("students.fullName")}><Input name="fullName" required /></Field>
              <div className="grid grid-cols-2 gap-3">
                <Field label={t("students.birthYear")}><Input name="birthYear" type="number" min={1900} max={2100} /></Field>
                <Field label={t("fields.phone")}><Input name="phone" type="tel" /></Field>
              </div>
              <Field label={t("students.guardian")}><Input name="guardianName" /></Field>
              <Field label={t("fields.email")}><Input name="email" type="email" /></Field>
              <Field label={t("classes.branch")}>
                <NativeSelect name="homeBranchId">
                  <option value="">{t("students.noBranch")}</option>
                  {branches.map((b) => <option key={b.id} value={b.id}>{localized(b.name, locale)}</option>)}
                </NativeSelect>
              </Field>
              <CheckRow name="createAccount" defaultChecked>{t("students.createAccount")}</CheckRow>
            </ActionForm>
          </FormSheet>
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
                <TableHead>{t("classes.branch")}</TableHead>
                <TableHead className="pr-5">{t("students.account")}</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {students.map((s) => (
                <TableRow key={s.id}>
                  <TableCell className="pl-5 font-medium">{s.fullName}</TableCell>
                  <TableCell className="tabular-nums">{s.birthYear}</TableCell>
                  <TableCell>{s.homeBranchId ? branchName.get(s.homeBranchId) : <span className="text-muted-foreground">{t("students.noBranch")}</span>}</TableCell>
                  <TableCell className="pr-5">
                    {s.userId ? (
                      <StatusBadge tone="success"><KeyRound className="size-3" />{t("yes")}</StatusBadge>
                    ) : (
                      <StatusBadge>{t("no")}</StatusBadge>
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
