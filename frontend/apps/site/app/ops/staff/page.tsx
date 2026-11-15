import { getFormatter, getTranslations } from "next-intl/server";
import { Users } from "lucide-react";
import { requireMe } from "@konnyaku/session";
import { Input } from "@konnyaku/ui/components/ui/input";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@konnyaku/ui/components/ui/table";
import { EmptyState, PageHeader, Panel, StatusBadge } from "@konnyaku/ui/kk/blocks";
import { CheckRow, Field } from "@konnyaku/ui/kk/field";
import { ActionForm, FormSheet } from "../action-form";
import { getJson, staffRoles, type StaffMember } from "../lib";

type Rate = { teacherId: string; hourlyRate: number; currency: string; effectiveFrom: string };

export default async function StaffPage() {
  const { token } = await requireMe("staff");
  const t = await getTranslations("admin");
  const format = await getFormatter();
  const [staff, rates] = await Promise.all([
    getJson<StaffMember[]>("/api/identity/staff", token, []),
    getJson<Rate[]>("/api/hr/rates", token, []),
  ]);
  // Rates come newest first per teacher: the first one seen is the latest.
  const latestRate = new Map<string, Rate>();
  for (const rate of rates) if (!latestRate.has(rate.teacherId)) latestRate.set(rate.teacherId, rate);

  return (
    <div className="space-y-6">
      <PageHeader
        title={t("staff.title")}
        description={t("staff.description")}
        actions={
          <FormSheet triggerLabel={t("staff.create")} title={t("staff.create")}>
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
                <TableHead>{t("staff.rate")}</TableHead>
                <TableHead className="pr-5 text-right" />
              </TableRow>
            </TableHeader>
            <TableBody>
              {staff.map((member) => {
                const rate = latestRate.get(member.userId);
                const teacher = member.roles.includes("Teacher");
                return (
                  <TableRow key={member.userId}>
                    <TableCell className="pl-5">
                      <div className="flex items-center gap-3">
                        <span className="grid size-9 shrink-0 place-items-center rounded-full bg-primary/10 text-xs font-semibold text-primary">
                          {member.displayName.split(/\s+/).slice(-1)[0]?.[0]?.toUpperCase()}
                        </span>
                        <div>
                          <p className="font-medium">{member.displayName}</p>
                          <p className="text-xs text-muted-foreground">{member.email}</p>
                        </div>
                      </div>
                    </TableCell>
                    <TableCell>
                      <div className="flex flex-wrap gap-1">
                        {member.roles.map((role) => (
                          <StatusBadge key={role} tone={role === "TenantAdmin" ? "primary" : role === "Teacher" ? "success" : "muted"}>
                            {t(`roles.${role}`)}
                          </StatusBadge>
                        ))}
                      </div>
                    </TableCell>
                    <TableCell>
                      {teacher &&
                        (rate ? (
                          <span className="tabular-nums">
                            {t("staff.rateFrom", {
                              rate: format.number(rate.hourlyRate, { style: "currency", currency: rate.currency }),
                              from: rate.effectiveFrom,
                            })}
                          </span>
                        ) : (
                          <StatusBadge tone="warning">{t("staff.noRate")}</StatusBadge>
                        ))}
                    </TableCell>
                    <TableCell className="pr-5 text-right">
                      {teacher && (
                        <FormSheet
                          triggerLabel={t("staff.setRate")}
                          title={t("staff.setRateFor", { name: member.displayName })}
                          description={t("staff.rateHint")}
                          variant="outline"
                          size="sm"
                          icon={false}
                        >
                          <ActionForm api={`hr/teachers/${member.userId}/rate`} method="PUT" submitLabel={t("save")}>
                            <Field label={t("staff.hourlyRate")}>
                              <Input name="hourlyRate" type="number" min={0} step={1000} required defaultValue={rate?.hourlyRate} />
                            </Field>
                            <Field label={t("fields.currency")}>
                              <Input name="currency" required defaultValue={rate?.currency ?? "VND"} maxLength={3} />
                            </Field>
                            <Field label={t("staff.effectiveFrom")}><Input name="effectiveFrom" type="date" required /></Field>
                          </ActionForm>
                        </FormSheet>
                      )}
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
