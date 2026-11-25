import { getFormatter, getLocale, getTranslations } from "next-intl/server";
import { Inbox, Mail, Phone } from "lucide-react";
import { requireMe } from "@konnyaku/session";
import { Input } from "@konnyaku/ui/components/ui/input";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@konnyaku/ui/components/ui/table";
import { EmptyState, PageHeader, Panel, StatusBadge } from "@konnyaku/ui/kk/blocks";
import { Field, NativeSelect } from "@konnyaku/ui/kk/field";
import { ActionButton, ActionForm, FormSheet } from "../action-form";
import { ChangeHistory } from "../history";
import { getAccess, getJson, localized, statusTone, type Branch, type Localized, type StaffMember } from "../lib";

type Lead = {
  id: string;
  studentName: string;
  birthYear: number | null;
  guardianName: string | null;
  phone: string | null;
  email: string | null;
  languageCode: string;
  goal: string | null;
  status: string;
  assignedConsultantId: string | null;
  followUpDueAt: string | null;
  preferredBranchId: string | null;
  preferredSchedule: string | null;
};
type Language = { code: string; name: Localized };

const nextStatuses = ["Contacted", "Converted", "Lost"] as const;

export default async function LeadsPage() {
  const { token } = await requireMe("staff");
  const t = await getTranslations("ops");
  const admin = await getTranslations("admin");
  const format = await getFormatter();
  const locale = await getLocale();
  const access = await getAccess(token);
  // A branch manager follows up the leads of their branches (the list holds only those).
  const canEdit = access.any("crm.leads");
  const [leads, languages, branches, staff] = await Promise.all([
    getJson<Lead[]>("/api/crm/leads", token, []),
    getJson<Language[]>("/api/catalog/languages", token, []),
    getJson<Branch[]>("/api/organization/branches", token, []),
    canEdit ? getJson<StaffMember[]>("/api/identity/staff", token, []) : Promise.resolve<StaffMember[]>([]),
  ]);
  const consultants = staff.filter((s) => s.roles.includes("Consultant") && s.status === "Active");
  const names: Record<string, string> = Object.fromEntries([
    ...branches.map((b) => [b.id, localized(b.name, locale)]),
    ...staff.map((s) => [s.userId, s.displayName]),
  ]);

  return (
    <div className="space-y-6">
      <PageHeader title={t("openLeads")} description={t("leadsDescription")} />
      <Panel bodyClassName="p-0">
        {leads.length === 0 ? (
          <div className="p-5"><EmptyState icon={Inbox} title={t("noLeads")} /></div>
        ) : (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead className="pl-5">{t("colName")}</TableHead>
                <TableHead>{t("colContact")}</TableHead>
                <TableHead className="hidden md:table-cell">{t("colLanguage")}</TableHead>
                <TableHead>{t("colStatus")}</TableHead>
                <TableHead className="hidden lg:table-cell">{t("colDue")}</TableHead>
                <TableHead className="pr-5 text-right">{t("colActions")}</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {leads.map((lead) => (
                <TableRow key={lead.id}>
                  <TableCell className="pl-5">
                    <p className="font-medium">{lead.studentName}</p>
                    {lead.goal && <p className="max-w-56 truncate text-xs text-muted-foreground">{lead.goal}</p>}
                  </TableCell>
                  <TableCell>
                    {lead.phone && (
                      <a href={`tel:${lead.phone}`} className="flex items-center gap-1.5 hover:text-primary">
                        <Phone className="size-3.5" />
                        {lead.phone}
                      </a>
                    )}
                    {lead.email && (
                      <a href={`mailto:${lead.email}`} className="flex items-center gap-1.5 text-muted-foreground hover:text-primary">
                        <Mail className="size-3.5" />
                        {lead.email}
                      </a>
                    )}
                  </TableCell>
                  <TableCell className="hidden md:table-cell"><span className="rounded bg-muted px-1.5 py-0.5 text-xs font-semibold uppercase">{lead.languageCode}</span></TableCell>
                  <TableCell><StatusBadge tone={statusTone[lead.status]}>{admin(`leadStatus.${lead.status}`)}</StatusBadge></TableCell>
                  <TableCell className="hidden text-muted-foreground tabular-nums lg:table-cell">
                    {lead.followUpDueAt ? format.dateTime(new Date(lead.followUpDueAt), { dateStyle: "short", timeStyle: "short" }) : null}
                  </TableCell>
                  <TableCell className="pr-5">
                    {canEdit && access.can("crm.leads", lead.preferredBranchId) && (
                      <div className="flex flex-wrap justify-end gap-1.5">
                        {nextStatuses
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
                            <div className="grid grid-cols-2 gap-3">
                              <Field label={admin("students.birthYear")}><Input name="birthYear" type="number" min={1900} max={2100} defaultValue={lead.birthYear ?? undefined} /></Field>
                              <Field label={admin("fields.phone")}><Input name="phone" type="tel" defaultValue={lead.phone ?? undefined} /></Field>
                            </div>
                            <Field label={admin("students.guardian")}><Input name="guardianName" defaultValue={lead.guardianName ?? undefined} /></Field>
                            <Field label={admin("fields.email")}><Input name="email" type="email" defaultValue={lead.email ?? undefined} /></Field>
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
                          <section className="mt-6 space-y-3">
                            <h3 className="text-sm font-semibold">{admin("history.title")}</h3>
                            <ChangeHistory sources={[{ module: "crm", id: lead.id }]} names={names} />
                          </section>
                        </FormSheet>
                      </div>
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
