import { getFormatter, getLocale, getTranslations } from "next-intl/server";
import { Inbox, SearchX } from "lucide-react";
import { requireMe } from "@konnyaku/session";
import { Input } from "@konnyaku/ui/components/ui/input";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@konnyaku/ui/components/ui/table";
import { EmptyState, PageHeader, Panel, StatusBadge } from "@konnyaku/ui/kk/blocks";
import { Field, NativeSelect } from "@konnyaku/ui/kk/field";
import { ActionButton, ActionForm, FormSheet } from "../action-form";
import { leadFilters } from "../filters";
import { ChangeHistory } from "../history";
import { emptyPage, getAccess, getJson, listQuery, localized, statusTone, type Branch, type Localized, type Page, type SearchParams, type StaffMember } from "../lib";
import { FilterDate, FilterSelect, ListBody, ListFrame, ListToolbar, Pagination, SortHeader } from "../list";
import { ContactFields, ListExport, RevealContact } from "../personal-data";

/** A row of the lead list: birth year, guardian, phone and email arrive masked. */
type Lead = {
  id: string;
  studentName: string;
  birthYear: string | null;
  guardianName: string | null;
  phone: string | null;
  email: string | null;
  languageCode: string;
  goal: string | null;
  status: string;
  assignedConsultantId: string | null;
  followUpDueAt: string | null;
  createdAt: string;
  preferredBranchId: string | null;
  preferredSchedule: string | null;
  anonymized: boolean;
};
type Language = { code: string; name: Localized };

const nextStatuses = ["Contacted", "Converted", "Lost"] as const;
const statuses = ["New", "Contacted", "Converted", "Lost"] as const;

export default async function LeadsPage({ searchParams }: { searchParams: SearchParams }) {
  const { token } = await requireMe("staff");
  const t = await getTranslations("ops");
  const admin = await getTranslations("admin");
  const format = await getFormatter();
  const locale = await getLocale();
  const access = await getAccess(token);
  // A branch manager follows up the leads of their branches (the list holds only those).
  const canEdit = access.any("crm.leads");
  const canExport = access.any("crm.export");
  const query = await listQuery(searchParams, leadFilters);
  const [leads, languages, branches, staff] = await Promise.all([
    getJson<Page<Lead>>(`/api/crm/leads?${query.toString()}`, token, emptyPage()),
    getJson<Language[]>("/api/catalog/languages", token, []),
    getJson<Branch[]>("/api/organization/branches", token, []),
    canEdit ? getJson<Page<StaffMember>>("/api/identity/staff?pageSize=200", token, emptyPage()) : Promise.resolve(emptyPage<StaffMember>()),
  ]);
  const consultants = staff.items.filter((s) => s.roles.includes("Consultant") && s.status === "Active");
  const names: Record<string, string> = Object.fromEntries([
    ...branches.map((b) => [b.id, localized(b.name, locale)]),
    ...staff.items.map((s) => [s.userId, s.displayName]),
  ]);
  const filtered = leadFilters.some((key) => query.has(key));

  return (
    <div className="space-y-6">
      <PageHeader title={t("leads")} description={t("leadsDescription")} />
      <ListFrame>
        <ListToolbar placeholder={admin("leads.search")} actions={canExport && <ListExport kind="leads" filterKeys={leadFilters} />}>
          <FilterSelect
            name="status"
            label={t("colStatus")}
            fallback="open"
            options={[
              { value: "open", label: admin("leads.open") },
              ...statuses.map((s) => ({ value: s, label: admin(`leadStatus.${s}`) })),
              { value: "all", label: admin("list.allStatuses") },
            ]}
          />
          <FilterSelect
            name="language"
            label={t("colLanguage")}
            allLabel={admin("list.allLanguages")}
            options={languages.map((l) => ({ value: l.code, label: localized(l.name, locale) }))}
          />
          <FilterSelect
            name="branch"
            label={admin("leads.preferredBranch")}
            allLabel={admin("list.allBranches")}
            options={[...branches.map((b) => ({ value: b.id, label: localized(b.name, locale) })), { value: "none", label: admin("students.noBranch") }]}
          />
          {consultants.length > 0 && (
            <FilterSelect
              name="consultant"
              label={admin("leads.consultant")}
              allLabel={admin("list.allConsultants")}
              options={consultants.map((c) => ({ value: c.userId, label: c.displayName }))}
            />
          )}
          <FilterDate name="createdFrom" label={admin("list.from")} />
          <FilterDate name="createdTo" label={admin("list.to")} />
        </ListToolbar>
        <ListBody>
          <Panel bodyClassName="p-0">
            {leads.items.length === 0 ? (
              <div className="p-5">{filtered ? <EmptyState icon={SearchX} title={admin("list.noResults")} /> : <EmptyState icon={Inbox} title={t("noLeads")} />}</div>
            ) : (
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead className="pl-5"><SortHeader field="name" label={t("colName")} defaultField="due" /></TableHead>
                    <TableHead>{t("colContact")}</TableHead>
                    <TableHead className="hidden md:table-cell">{t("colLanguage")}</TableHead>
                    <TableHead>{t("colStatus")}</TableHead>
                    <TableHead className="hidden lg:table-cell"><SortHeader field="due" label={t("colDue")} defaultField="due" /></TableHead>
                    <TableHead className="pr-5 text-right">{t("colActions")}</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {leads.items.map((lead) => {
                    const mayEdit = canEdit && !lead.anonymized && access.can("crm.leads", lead.preferredBranchId);
                    return (
                      <TableRow key={lead.id}>
                        <TableCell className="pl-5 align-top">
                          <p className="font-medium">
                            {lead.anonymized ? <span className="text-muted-foreground">{admin("privacy.anonymizedName", { id: lead.studentName })}</span> : lead.studentName}
                          </p>
                          {lead.goal && <p className="max-w-56 truncate text-xs text-muted-foreground">{lead.goal}</p>}
                          {lead.birthYear && <p className="font-mono text-xs text-muted-foreground">{admin("privacy.birthYear", { year: lead.birthYear })}</p>}
                        </TableCell>
                        <TableCell className="align-top">
                          <RevealContact kind="leads" id={lead.id} masked={lead} canReveal={mayEdit} />
                        </TableCell>
                        <TableCell className="hidden align-top md:table-cell"><span className="rounded bg-muted px-1.5 py-0.5 text-xs font-semibold uppercase">{lead.languageCode}</span></TableCell>
                        <TableCell className="align-top"><StatusBadge tone={statusTone[lead.status]}>{admin(`leadStatus.${lead.status}`)}</StatusBadge></TableCell>
                        <TableCell className="hidden align-top text-muted-foreground tabular-nums lg:table-cell">
                          {lead.followUpDueAt ? format.dateTime(new Date(lead.followUpDueAt), { dateStyle: "short", timeStyle: "short" }) : null}
                        </TableCell>
                        <TableCell className="pr-5 align-top">
                          {mayEdit && (
                            <div className="flex flex-wrap justify-end gap-1.5">
                              {(lead.status === "New" || lead.status === "Contacted") &&
                                nextStatuses
                                  .filter((status) => status !== lead.status)
                                  .map((status) => (
                                    <ActionButton
                                      key={status}
                                      api={`crm/leads/${lead.id}/status`}
                                      body={{ status }}
                                      label={admin(`leadActions.${status}`)}
                                      variant={status === "Converted" ? "default" : status === "Lost" ? "ghost" : "outline"}
                                    />
                                  ))}
                              <FormSheet triggerLabel={admin("edit")} title={admin("leads.edit", { name: lead.studentName })} variant="ghost" size="sm" icon="edit">
                                <ActionForm api={`crm/leads/${lead.id}`} method="PUT" submitLabel={admin("save")}>
                                  <Field label={admin("students.fullName")}><Input name="studentName" required defaultValue={lead.studentName} /></Field>
                                  <ContactFields kind="leads" id={lead.id} guardianLabel={admin("students.guardian")} />
                                  <Field label={admin("programs.language")}>
                                    <NativeSelect name="languageCode" required defaultValue={lead.languageCode}>
                                      {languages.map((l) => <option key={l.code} value={l.code}>{localized(l.name, locale)}</option>)}
                                    </NativeSelect>
                                  </Field>
                                  <Field label={admin("leads.goal")}><Input name="goal" defaultValue={lead.goal ?? undefined} /></Field>
                                  <Field label={admin("leads.preferredBranch")}>
                                    <NativeSelect name="preferredBranchId" defaultValue={lead.preferredBranchId ?? ""}>
                                      <option value="">{admin("students.noBranch")}</option>
                                      {branches
                                        .filter((b) => b.id === lead.preferredBranchId || access.can("crm.leads", b.id))
                                        .map((b) => <option key={b.id} value={b.id}>{localized(b.name, locale)}</option>)}
                                    </NativeSelect>
                                  </Field>
                                  <Field label={admin("leads.preferredSchedule")}><Input name="preferredSchedule" defaultValue={lead.preferredSchedule ?? undefined} /></Field>
                                  <Field label={admin("leads.consultant")}>
                                    <NativeSelect name="assignedConsultantId" defaultValue={lead.assignedConsultantId ?? ""}>
                                      <option value="">{admin("leads.noConsultant")}</option>
                                      {consultants.map((c) => <option key={c.userId} value={c.userId}>{c.displayName}</option>)}
                                    </NativeSelect>
                                  </Field>
                                </ActionForm>
                                <p className="mt-3 text-xs text-muted-foreground">{admin("privacy.editLogged")}</p>
                                <section className="mt-6 space-y-3">
                                  <h3 className="text-sm font-semibold">{admin("history.title")}</h3>
                                  <ChangeHistory sources={[{ module: "crm", id: lead.id }]} names={names} />
                                </section>
                              </FormSheet>
                            </div>
                          )}
                        </TableCell>
                      </TableRow>
                    );
                  })}
                </TableBody>
              </Table>
            )}
          </Panel>
        </ListBody>
        <Pagination total={leads.total} page={leads.pageNumber} pageSize={leads.pageSize} />
      </ListFrame>
    </div>
  );
}
