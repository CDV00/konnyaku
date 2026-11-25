import { getLocale, getTranslations } from "next-intl/server";
import { GraduationCap, KeyRound, SearchX } from "lucide-react";
import { requireMe } from "@konnyaku/session";
import { Input } from "@konnyaku/ui/components/ui/input";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@konnyaku/ui/components/ui/table";
import { EmptyState, PageHeader, Panel, StatusBadge } from "@konnyaku/ui/kk/blocks";
import { CheckRow, Field, NativeSelect } from "@konnyaku/ui/kk/field";
import { ActionButton, ActionForm, FormSheet } from "../action-form";
import { ChangeHistory } from "../history";
import { emptyPage, getAccess, getJson, listQuery, localized, type Branch, type Page, type SearchParams, type Student } from "../lib";
import { FilterDate, FilterSelect, ListBody, ListFrame, ListToolbar, Pagination, SortHeader } from "../list";
import { ContactFields, ListExport, RevealContact } from "../personal-data";
import { studentFilters } from "../filters";

const locales = ["vi", "en"] as const;


export default async function StudentsPage({ searchParams }: { searchParams: SearchParams }) {
  const { token } = await requireMe("staff");
  const t = await getTranslations("admin");
  const locale = await getLocale();
  const access = await getAccess(token);
  // Student records are center-wide: any grant of the permission (a branch manager's too) covers them.
  const canEdit = access.any("crm.students");
  const canExport = access.any("crm.export");
  const query = await listQuery(searchParams, studentFilters);
  const [students, branches] = await Promise.all([
    getJson<Page<Student>>(`/api/crm/students?${query.toString()}`, token, emptyPage()),
    getJson<Branch[]>("/api/organization/branches", token, []),
  ]);
  const branchName = new Map(branches.map((b) => [b.id, localized(b.name, locale)]));
  const names = Object.fromEntries(branchName.entries());
  const filtered = studentFilters.some((key) => query.has(key));
  const branchSelect = (current?: string | null) => (
    <Field label={t("classes.branch")}>
      <NativeSelect name="homeBranchId" defaultValue={current ?? ""}>
        <option value="">{t("students.noBranch")}</option>
        {branches.filter((b) => b.isActive || b.id === current).map((b) => <option key={b.id} value={b.id}>{localized(b.name, locale)}</option>)}
      </NativeSelect>
    </Field>
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
                <div className="grid grid-cols-2 gap-3">
                  <Field label={t("students.birthYear")}><Input name="birthYear" type="number" min={1900} max={2100} /></Field>
                  <Field label={t("fields.phone")}><Input name="phone" type="tel" /></Field>
                </div>
                <Field label={t("students.guardian")}><Input name="guardianName" /></Field>
                <Field label={t("fields.email")}><Input name="email" type="email" /></Field>
                {branchSelect()}
                <CheckRow name="createAccount" defaultChecked>{t("students.createAccount")}</CheckRow>
              </ActionForm>
            </FormSheet>
          )
        }
      />
      <ListFrame>
        <ListToolbar placeholder={t("students.search")} actions={canExport && <ListExport kind="students" filterKeys={studentFilters} />}>
          <FilterSelect
            name="status"
            label={t("fields.status")}
            allLabel={t("list.allStatuses")}
            options={[{ value: "active", label: t("active") }, { value: "inactive", label: t("inactive") }]}
          />
          <FilterSelect
            name="branch"
            label={t("classes.branch")}
            allLabel={t("list.allBranches")}
            options={[...branches.map((b) => ({ value: b.id, label: localized(b.name, locale) })), { value: "none", label: t("students.noBranch") }]}
          />
          <FilterSelect
            name="hasAccount"
            label={t("students.account")}
            allLabel={t("list.anyAccount")}
            options={[{ value: "true", label: t("students.withAccount") }, { value: "false", label: t("students.withoutAccount") }]}
          />
          <FilterDate name="createdFrom" label={t("list.from")} />
          <FilterDate name="createdTo" label={t("list.to")} />
        </ListToolbar>
        <ListBody>
          <Panel bodyClassName="p-0">
            {students.items.length === 0 ? (
              <div className="p-5">
                {filtered ? <EmptyState icon={SearchX} title={t("list.noResults")} /> : <EmptyState icon={GraduationCap} title={t("students.empty")} />}
              </div>
            ) : (
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead className="pl-5"><SortHeader field="name" label={t("students.fullName")} defaultField="name" /></TableHead>
                    <TableHead><SortHeader field="birthYear" label={t("students.birthYear")} defaultField="name" /></TableHead>
                    <TableHead className="hidden md:table-cell">{t("classes.branch")}</TableHead>
                    <TableHead className="hidden sm:table-cell">{t("students.account")}</TableHead>
                    <TableHead className="hidden lg:table-cell"><SortHeader field="createdAt" label={t("students.createdAt")} defaultField="name" /></TableHead>
                    <TableHead className="pr-5" />
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {students.items.map((s) => (
                    <TableRow key={s.id} className={s.isActive ? "" : "opacity-70"}>
                      <TableCell className="pl-5 align-top">
                        <p className="flex flex-wrap items-center gap-2 font-medium">
                          {s.anonymized ? <span className="text-muted-foreground">{t("privacy.anonymizedName", { id: s.fullName })}</span> : s.fullName}
                          {!s.isActive && <StatusBadge tone="muted">{t("inactive")}</StatusBadge>}
                        </p>
                        <RevealContact kind="students" id={s.id} masked={s} canReveal={canEdit && !s.anonymized} />
                      </TableCell>
                      <TableCell className="align-top font-mono text-sm tabular-nums">{s.birthYear}</TableCell>
                      <TableCell className="hidden align-top md:table-cell">
                        {s.homeBranchId ? branchName.get(s.homeBranchId) : <span className="text-muted-foreground">{t("students.noBranch")}</span>}
                      </TableCell>
                      <TableCell className="hidden align-top sm:table-cell">
                        {s.userId ? (
                          <StatusBadge tone="success"><KeyRound className="size-3" />{t("yes")}</StatusBadge>
                        ) : (
                          <StatusBadge>{t("no")}</StatusBadge>
                        )}
                      </TableCell>
                      <TableCell className="hidden align-top text-sm text-muted-foreground tabular-nums lg:table-cell">{s.createdAt.slice(0, 10)}</TableCell>
                      <TableCell className="pr-5 text-right align-top">
                        {canEdit && !s.anonymized && (
                          <span className="inline-flex flex-wrap items-start justify-end gap-2">
                            <FormSheet triggerLabel={t("edit")} title={t("students.edit", { name: s.fullName })} variant="ghost" size="sm" icon="edit">
                              <ActionForm api={`crm/students/${s.id}`} method="PUT" submitLabel={t("save")}>
                                <Field label={t("students.fullName")}><Input name="fullName" required defaultValue={s.fullName} /></Field>
                                <ContactFields kind="students" id={s.id} guardianLabel={t("students.guardian")} />
                                {branchSelect(s.homeBranchId)}
                                <Field label={t("students.locale")}>
                                  <NativeSelect name="communicationLocale" defaultValue={s.communicationLocale}>
                                    {locales.map((l) => <option key={l} value={l}>{t(`locales.${l}`)}</option>)}
                                  </NativeSelect>
                                </Field>
                              </ActionForm>
                              <p className="mt-3 text-xs text-muted-foreground">{t("privacy.editLogged")}</p>
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
        </ListBody>
        <Pagination total={students.total} page={students.pageNumber} pageSize={students.pageSize} />
      </ListFrame>
    </div>
  );
}
