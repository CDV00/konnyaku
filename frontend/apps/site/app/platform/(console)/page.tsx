import Link from "next/link";
import { headers } from "next/headers";
import { getFormatter, getTranslations } from "next-intl/server";
import { Building2, CirclePause, GraduationCap, Users } from "lucide-react";
import { Fade } from "@konnyaku/ui/components/animate-ui/primitives/effects/fade";
import { Input } from "@konnyaku/ui/components/ui/input";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@konnyaku/ui/components/ui/table";
import { EmptyState, PageHeader, Panel, StatCard, StatusBadge } from "@konnyaku/ui/kk/blocks";
import { CheckRow, Field, NativeSelect } from "@konnyaku/ui/kk/field";
import { baseDomain, siteUrl } from "../../../platform-host";
import { consoleGet, requireActiveOperator, statusTone, type TenantSummary } from "../lib";
import { ConsoleForm, ConsoleSheet } from "../platform-form";

const uiLocales = ["vi", "en"] as const;
const commonTimeZones = ["Asia/Ho_Chi_Minh", "Asia/Bangkok", "Asia/Singapore", "Asia/Tokyo", "Asia/Seoul", "Europe/London", "UTC"];

export default async function TenantsPage() {
  const { token } = await requireActiveOperator();
  const t = await getTranslations("platform");
  const format = await getFormatter();
  const consoleHost = (await headers()).get("host");
  const tenants = await consoleGet<TenantSummary[]>("tenants", token, []);
  const suspended = tenants.filter((x) => x.status === "Suspended").length;
  const students = tenants.reduce((sum, x) => sum + (x.usage?.students ?? 0), 0);
  const staff = tenants.reduce((sum, x) => sum + (x.usage?.staff ?? 0), 0);

  return (
    <div className="space-y-6">
      <PageHeader
        title={t("tenants.title")}
        description={t("tenants.description")}
        actions={
          <ConsoleSheet triggerLabel={t("tenants.create")} title={t("tenants.create")} description={t("tenants.createHint")}>
            <ConsoleForm api="tenants" submitLabel={t("tenants.create")} showResult={["slug", "host", "adminEmail", "temporaryPassword"]}>
              <fieldset className="grid gap-4">
                <legend className="mb-2 text-sm font-semibold">{t("tenants.centerSection")}</legend>
                <Field label={t("tenants.name")}><Input name="name" required maxLength={200} /></Field>
                <Field label={t("tenants.slug")} hint={t("tenants.slugHint", { example: `slug.${baseDomain}` })}>
                  <Input name="slug" required pattern="[a-z0-9][a-z0-9\-]{1,38}[a-z0-9]" autoCapitalize="none" spellCheck={false} />
                </Field>
                <Field label={t("tenants.defaultLocale")}>
                  <NativeSelect name="defaultLocale" defaultValue="vi">
                    {uiLocales.map((l) => <option key={l} value={l}>{t(`tenants.localeName.${l}`)}</option>)}
                  </NativeSelect>
                </Field>
                <fieldset className="grid gap-2 rounded-lg border p-3">
                  <legend className="px-1 text-sm font-medium">{t("tenants.locales")}</legend>
                  {uiLocales.map((l) => (
                    <CheckRow key={l} name="enabledLocales[]" value={l} defaultChecked>{t(`tenants.localeName.${l}`)}</CheckRow>
                  ))}
                </fieldset>
                <Field label={t("tenants.timeZone")} hint={t("tenants.timeZoneHint")}>
                  <Input name="timeZone" required defaultValue="Asia/Ho_Chi_Minh" list="platform-time-zones" spellCheck={false} />
                </Field>
                <datalist id="platform-time-zones">
                  {commonTimeZones.map((zone) => <option key={zone} value={zone} />)}
                </datalist>
              </fieldset>
              <fieldset className="grid gap-4 border-t pt-4">
                <legend className="mb-2 pt-4 text-sm font-semibold">{t("tenants.adminSection")}</legend>
                <Field label={t("tenants.adminName")}><Input name="adminName" required /></Field>
                <Field label={t("tenants.adminEmail")}><Input name="adminEmail" type="email" required /></Field>
              </fieldset>
            </ConsoleForm>
          </ConsoleSheet>
        }
      />

      <Fade inView className="grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
        <StatCard label={t("tenants.title")} count={tenants.length} icon={Building2} />
        <StatCard label={t("status.Suspended")} count={suspended} icon={CirclePause} tone={suspended > 0 ? "destructive" : "muted"} />
        <StatCard label={t("tenants.columns.students")} count={students} icon={GraduationCap} tone="success" />
        <StatCard label={t("tenants.columns.staff")} count={staff} icon={Users} tone="highlight" />
      </Fade>

      <Panel bodyClassName="p-0" title={t("tenants.summary", { total: tenants.length, suspended })}>
        {tenants.length === 0 ? (
          <div className="p-5"><EmptyState icon={Building2} title={t("tenants.empty")} /></div>
        ) : (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead className="pl-5">{t("tenants.columns.center")}</TableHead>
                <TableHead className="hidden md:table-cell">{t("tenants.columns.host")}</TableHead>
                <TableHead>{t("tenants.columns.status")}</TableHead>
                <TableHead className="hidden text-right sm:table-cell">{t("tenants.columns.students")}</TableHead>
                <TableHead className="hidden text-right lg:table-cell">{t("tenants.columns.classes")}</TableHead>
                <TableHead className="hidden text-right lg:table-cell">{t("tenants.columns.staff")}</TableHead>
                <TableHead className="hidden pr-5 xl:table-cell">{t("tenants.columns.created")}</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {tenants.map((tenant) => (
                <TableRow key={tenant.id}>
                  <TableCell className="pl-5">
                    <Link href={`/platform/tenants/${tenant.id}`} className="group block">
                      <span className="font-medium group-hover:text-primary group-hover:underline">{tenant.name}</span>
                      <span className="block font-mono text-xs text-muted-foreground">{tenant.slug}</span>
                    </Link>
                  </TableCell>
                  <TableCell className="hidden md:table-cell">
                    <a href={siteUrl(tenant.primaryHost, consoleHost)} target="_blank" rel="noreferrer" className="font-mono text-xs hover:text-primary hover:underline">
                      {tenant.primaryHost}
                    </a>
                    {tenant.hosts.length > 1 && <span className="ms-1 text-xs text-muted-foreground">{t("tenants.moreHosts", { count: tenant.hosts.length - 1 })}</span>}
                  </TableCell>
                  <TableCell><StatusBadge tone={statusTone[tenant.status]}>{t(`status.${tenant.status}`)}</StatusBadge></TableCell>
                  {tenant.usage ? (
                    <>
                      <TableCell className="hidden text-right tabular-nums sm:table-cell">{format.number(tenant.usage.students)}</TableCell>
                      <TableCell className="hidden text-right tabular-nums lg:table-cell">{format.number(tenant.usage.activeClasses)}</TableCell>
                      <TableCell className="hidden text-right tabular-nums lg:table-cell">{format.number(tenant.usage.staff)}</TableCell>
                    </>
                  ) : (
                    <TableCell colSpan={3} className="hidden text-right text-xs text-muted-foreground sm:table-cell">{t("tenants.usageUnavailable")}</TableCell>
                  )}
                  <TableCell className="hidden pr-5 text-muted-foreground xl:table-cell">{format.dateTime(new Date(tenant.createdAt), { dateStyle: "medium" })}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </Panel>
    </div>
  );
}
