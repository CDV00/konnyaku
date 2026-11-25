import { getFormatter, getLocale, getTranslations } from "next-intl/server";
import { Users } from "lucide-react";
import { requireMe } from "@konnyaku/session";
import { Input } from "@konnyaku/ui/components/ui/input";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@konnyaku/ui/components/ui/table";
import { EmptyState, PageHeader, Panel, StatusBadge } from "@konnyaku/ui/kk/blocks";
import { CheckRow, Field } from "@konnyaku/ui/kk/field";
import { ActionButton, ActionForm, FormSheet } from "../action-form";
import { ChangeHistory } from "../history";
import { getAccess, getJson, localized, staffRoles, statusTone, type Branch, type StaffMember } from "../lib";
import { RolesField } from "./roles-field";

type Rate = { id: string; teacherId: string; hourlyRate: number; currency: string; effectiveFrom: string };

export default async function StaffPage() {
  const { me, token } = await requireMe("staff");
  const t = await getTranslations("admin");
  const format = await getFormatter();
  const locale = await getLocale();
  const [staff, rates, branches, access] = await Promise.all([
    getJson<StaffMember[]>("/api/identity/staff", token, []),
    getJson<Rate[]>("/api/hr/rates", token, []),
    getJson<Branch[]>("/api/organization/branches", token, []),
    getAccess(token),
  ]);
  const canManage = access.can("identity.staff");
  const canRates = access.can("hr.rates");
  // Rates come newest first per teacher: the first one seen is the latest.
  const ratesOf = new Map<string, Rate[]>();
  for (const rate of rates) ratesOf.set(rate.teacherId, [...(ratesOf.get(rate.teacherId) ?? []), rate]);
  const branchName = new Map(branches.map((b) => [b.id, localized(b.name, locale)]));
  const branchOptions = branches.filter((b) => b.isActive).map((b) => ({ id: b.id, name: localized(b.name, locale) }));
  const names = Object.fromEntries([...branchName.entries()]);
  const money = (rate: Rate) => format.number(rate.hourlyRate, { style: "currency", currency: rate.currency });

  return (
    <div className="space-y-6">
      <PageHeader
        title={t("staff.title")}
        description={t("staff.description")}
        actions={
          canManage && (
            <FormSheet triggerLabel={t("staff.create")} title={t("staff.create")} description={t("staff.createHint")}>
              <ActionForm api="identity/staff" submitLabel={t("save")}>
                <Field label={t("fields.displayName")}><Input name="displayName" required /></Field>
                <Field label={t("fields.email")}><Input name="email" type="email" required /></Field>
                <Field label={t("staff.initialPassword")}>
                  <Input name="initialPassword" type="password" minLength={8} autoComplete="new-password" />
                </Field>
                <fieldset className="grid gap-2 rounded-lg border p-3">
                  <legend className="px-1 text-sm font-medium">{t("staff.roles")}</legend>
                  {staffRoles.map((role) => (
                    <CheckRow key={role} name="roles[]" value={role} defaultChecked={role === "Teacher"}>{t(`roles.${role}`)}</CheckRow>
                  ))}
                </fieldset>
              </ActionForm>
            </FormSheet>
          )
        }
      />
      <Panel bodyClassName="p-0">
        {staff.length === 0 ? (
          <div className="p-5"><EmptyState icon={Users} title={t("staff.empty")} /></div>
        ) : (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead className="pl-5">{t("fields.displayName")}</TableHead>
                <TableHead>{t("staff.roles")}</TableHead>
                <TableHead className="hidden md:table-cell">{t("staff.rate")}</TableHead>
                <TableHead className="pr-5 text-right" />
              </TableRow>
            </TableHeader>
            <TableBody>
              {staff.map((member) => {
                const memberRates = ratesOf.get(member.userId) ?? [];
                const rate = memberRates[0];
                const teacher = member.roles.includes("Teacher");
                const active = member.status === "Active";
                return (
                  <TableRow key={member.userId} className={active ? "" : "opacity-70"}>
                    <TableCell className="pl-5">
                      <div className="flex items-center gap-3">
                        <span className="grid size-9 shrink-0 place-items-center rounded-full bg-primary/10 text-xs font-semibold text-primary">
                          {member.displayName.split(/\s+/).slice(-1)[0]?.[0]?.toUpperCase()}
                        </span>
                        <div>
                          <p className="flex flex-wrap items-center gap-2 font-medium">
                            {member.displayName}
                            {!active && <StatusBadge tone={statusTone[member.status]}>{t(`staffStatus.${member.status}`)}</StatusBadge>}
                          </p>
                          <p className="text-xs text-muted-foreground">{member.email}</p>
                        </div>
                      </div>
                    </TableCell>
                    <TableCell>
                      <div className="flex flex-wrap gap-1">
                        {member.grants.map((grant) => (
                          <StatusBadge
                            key={`${grant.role}-${grant.branchId ?? "all"}`}
                            tone={grant.role === "TenantAdmin" ? "primary" : grant.role === "Teacher" ? "success" : "muted"}
                          >
                            {grant.branchId
                              ? t("staff.roleAt", { role: t(`roles.${grant.role}`), branch: branchName.get(grant.branchId) ?? "" })
                              : t(`roles.${grant.role}`)}
                          </StatusBadge>
                        ))}
                      </div>
                    </TableCell>
                    <TableCell className="hidden md:table-cell">
                      {teacher &&
                        (rate ? (
                          <span className="tabular-nums">{t("staff.rateFrom", { rate: money(rate), from: rate.effectiveFrom })}</span>
                        ) : (
                          <StatusBadge tone="warning">{t("staff.noRate")}</StatusBadge>
                        ))}
                    </TableCell>
                    <TableCell className="pr-5 text-right">
                      <span className="inline-flex flex-wrap items-start justify-end gap-2">
                        {teacher && canRates && (
                          <FormSheet
                            triggerLabel={t("staff.setRate")}
                            title={t("staff.setRateFor", { name: member.displayName })}
                            description={t("staff.rateHint")}
                            variant="outline"
                            size="sm"
                            icon={false}
                          >
                            <ActionForm api={`hr/teachers/${member.userId}/rate`} method="PUT" submitLabel={t("staff.addRate")}>
                              <Field label={t("staff.hourlyRate")}>
                                <Input name="hourlyRate" type="number" min={0} step={1000} required defaultValue={rate?.hourlyRate} />
                              </Field>
                              <Field label={t("fields.currency")}>
                                <Input name="currency" required defaultValue={rate?.currency ?? "VND"} maxLength={3} />
                              </Field>
                              <Field label={t("staff.effectiveFrom")}><Input name="effectiveFrom" type="date" required /></Field>
                            </ActionForm>
                            {memberRates.length > 0 && (
                              <section className="mt-6 space-y-3">
                                <h3 className="text-sm font-semibold">{t("staff.rateList")}</h3>
                                <p className="text-xs text-muted-foreground">{t("staff.rateEditHint")}</p>
                                {memberRates.map((r) => (
                                  <div key={r.id} className="grid gap-2 rounded-lg border p-3">
                                    <ActionForm api={`hr/rates/${r.id}`} method="PUT" submitLabel={t("save")} layout="inline">
                                      <Input name="hourlyRate" type="number" min={0} step={1000} required defaultValue={r.hourlyRate} className="w-32" aria-label={t("staff.hourlyRate")} />
                                      <Input name="currency" required defaultValue={r.currency} maxLength={3} className="w-16" aria-label={t("fields.currency")} />
                                      <Input name="effectiveFrom" type="date" required defaultValue={r.effectiveFrom} className="w-40" aria-label={t("staff.effectiveFrom")} />
                                    </ActionForm>
                                    <ActionButton api={`hr/rates/${r.id}`} method="DELETE" label={t("delete")} variant="ghost" confirm={t("staff.rateDeleteConfirm")} />
                                  </div>
                                ))}
                              </section>
                            )}
                            <section className="mt-6 space-y-3">
                              <h3 className="text-sm font-semibold">{t("history.title")}</h3>
                              <ChangeHistory sources={[{ module: "hr", id: member.userId }]} names={names} />
                            </section>
                          </FormSheet>
                        )}
                        {canManage && (
                          <FormSheet triggerLabel={t("edit")} title={t("staff.edit", { name: member.displayName })} variant="ghost" size="sm" icon="edit">
                            <ActionForm api={`identity/staff/${member.userId}`} method="PUT" submitLabel={t("save")}>
                              <Field label={t("fields.displayName")}><Input name="displayName" required defaultValue={member.displayName} /></Field>
                              <RolesField initial={member.grants} branches={branchOptions} />
                            </ActionForm>
                            <section className="mt-6 space-y-3">
                              <h3 className="text-sm font-semibold">{t("history.title")}</h3>
                              <ChangeHistory sources={[{ module: "identity", id: member.userId }]} names={names} />
                            </section>
                          </FormSheet>
                        )}
                        {canManage && member.userId !== me.userId && (
                          active ? (
                            <ActionButton api={`identity/staff/${member.userId}/deactivate`} label={t("deactivate")} variant="ghost" confirm={t("staff.deactivateConfirm")} />
                          ) : (
                            <ActionButton api={`identity/staff/${member.userId}/activate`} label={t("activate")} variant="ghost" />
                          )
                        )}
                      </span>
                    </TableCell>
                  </TableRow>
                );
              })}
            </TableBody>
          </Table>
        )}
      </Panel>
    </div>
  );
}
