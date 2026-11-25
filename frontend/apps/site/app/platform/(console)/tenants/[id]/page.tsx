import Link from "next/link";
import { headers } from "next/headers";
import { notFound } from "next/navigation";
import { getFormatter, getTranslations } from "next-intl/server";
import { ArrowLeft, ArrowUpRight, CalendarDays, GraduationCap, Users } from "lucide-react";
import { Input } from "@konnyaku/ui/components/ui/input";
import { PageHeader, Panel, StatCard, StatusBadge } from "@konnyaku/ui/kk/blocks";
import { Field } from "@konnyaku/ui/kk/field";
import { siteUrl } from "../../../../../platform-host";
import { AuditTable } from "../../../audit-table";
import { consoleGet, requireActiveOperator, statusTone, type AuditEntry, type TenantSummary } from "../../../lib";
import { ConsoleButton, ConsoleForm, ConsoleSheet } from "../../../platform-form";

export default async function TenantPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const { token } = await requireActiveOperator();
  const t = await getTranslations("platform");
  const format = await getFormatter();
  const consoleHost = (await headers()).get("host");
  const [tenant, audit] = await Promise.all([
    consoleGet<TenantSummary | null>(`tenants/${id}`, token, null),
    consoleGet<AuditEntry[]>(`audit?tenantId=${id}&take=20`, token, []),
  ]);
  if (!tenant) notFound();
  const suspended = tenant.status === "Suspended";

  return (
    <div className="space-y-6">
      <Link href="/platform" className="inline-flex items-center gap-1 text-sm text-muted-foreground hover:text-foreground">
        <ArrowLeft className="size-4" />
        {t("tenant.back")}
      </Link>
      <PageHeader
        title={tenant.name}
        description={
          <span className="flex flex-wrap items-center gap-2">
            <StatusBadge tone={statusTone[tenant.status]}>{t(`status.${tenant.status}`)}</StatusBadge>
            {suspended && tenant.suspendedAt && (
              <span>{t("tenant.suspendedSince", { date: format.dateTime(new Date(tenant.suspendedAt), { dateStyle: "medium", timeStyle: "short" }) })}</span>
            )}
          </span>
        }
        actions={
          <>
            <a
              href={siteUrl(tenant.primaryHost, consoleHost)}
              target="_blank"
              rel="noreferrer"
              className="inline-flex h-9 items-center gap-1 rounded-md border px-3 text-sm font-medium hover:bg-accent"
            >
              {t("tenant.open")}
              <ArrowUpRight className="size-4" />
            </a>
            {suspended ? (
              <ConsoleButton api={`tenants/${tenant.id}/resume`} label={t("tenant.resume")} variant="default" />
            ) : (
              <ConsoleSheet triggerLabel={t("tenant.suspend")} title={t("tenant.suspend")} description={t("tenant.suspendHint")} variant="destructive" icon={false}>
                <ConsoleForm api={`tenants/${tenant.id}/suspend`} submitLabel={t("tenant.suspend")} variant="destructive">
                  <Field label={t("tenant.reason")}>
                    <Input name="reason" maxLength={500} placeholder={t("tenant.reasonPlaceholder")} />
                  </Field>
                </ConsoleForm>
              </ConsoleSheet>
            )}
          </>
        }
      />

      {suspended && tenant.suspendedReason && (
        <p className="rounded-lg border border-destructive/30 bg-destructive/10 px-4 py-3 text-sm text-destructive">
          {t("tenant.reasonLine", { reason: tenant.suspendedReason })}
        </p>
      )}

      {tenant.usage && (
        <div className="grid gap-4 sm:grid-cols-3">
          <StatCard label={t("tenants.columns.students")} count={tenant.usage.students} icon={GraduationCap} tone="success" />
          <StatCard label={t("tenants.columns.classes")} count={tenant.usage.activeClasses} icon={CalendarDays} />
          <StatCard label={t("tenants.columns.staff")} count={tenant.usage.staff} icon={Users} tone="highlight" />
        </div>
      )}

      <div className="grid gap-6 lg:grid-cols-2">
        <Panel title={t("tenant.overview")}>
          <dl className="grid grid-cols-[auto_1fr] gap-x-6 gap-y-2 text-sm">
            <dt className="text-muted-foreground">{t("tenant.slug")}</dt>
            <dd className="font-mono">{tenant.slug}</dd>
            <dt className="text-muted-foreground">{t("tenant.locales")}</dt>
            <dd>{tenant.enabledLocales.map((l) => (l === tenant.defaultLocale ? `${l} (${t("tenant.primary")})` : l)).join(", ")}</dd>
            <dt className="text-muted-foreground">{t("tenant.timeZone")}</dt>
            <dd>{tenant.timeZone}</dd>
            <dt className="text-muted-foreground">{t("tenant.createdAt")}</dt>
            <dd>{format.dateTime(new Date(tenant.createdAt), { dateStyle: "medium", timeStyle: "short" })}</dd>
          </dl>
          <div className="mt-5 border-t pt-4">
            <ConsoleSheet triggerLabel={t("tenant.addAdmin")} title={t("tenant.addAdmin")} description={t("tenant.addAdminHint")} variant="outline" size="sm">
              <ConsoleForm api={`tenants/${tenant.id}/admins`} submitLabel={t("tenant.addAdmin")} showResult={["adminEmail", "temporaryPassword"]}>
                <Field label={t("tenants.adminName")}><Input name="displayName" required /></Field>
                <Field label={t("tenants.adminEmail")}><Input name="email" type="email" required /></Field>
              </ConsoleForm>
            </ConsoleSheet>
          </div>
        </Panel>

        <Panel
          title={t("tenant.hosts")}
          description={t("tenant.hostsHint")}
          action={
            <ConsoleSheet triggerLabel={t("tenant.addDomain")} title={t("tenant.addDomain")} description={t("tenant.hostsHint")} variant="outline" size="sm">
              <ConsoleForm api={`tenants/${tenant.id}/domains`} submitLabel={t("tenant.addDomain")}>
                <Field label={t("tenant.host")}>
                  <Input name="host" required placeholder={t("tenant.hostPlaceholder")} autoCapitalize="none" spellCheck={false} />
                </Field>
              </ConsoleForm>
            </ConsoleSheet>
          }
        >
          <ul className="grid gap-2">
            {tenant.hosts.map((host) => (
              <li key={host} className="flex flex-wrap items-center justify-between gap-2 rounded-lg border px-3 py-2">
                <a href={siteUrl(host, consoleHost)} target="_blank" rel="noreferrer" className="font-mono text-sm hover:text-primary hover:underline">{host}</a>
                {host === tenant.primaryHost ? (
                  <StatusBadge tone="primary">{t("tenant.primary")}</StatusBadge>
                ) : (
                  <ConsoleButton api={`tenants/${tenant.id}/domains/remove`} body={{ host }} label={t("tenant.removeDomain")} variant="ghost" />
                )}
              </li>
            ))}
          </ul>
        </Panel>
      </div>

      <Panel
        title={t("tenant.recentAudit")}
        bodyClassName="p-0"
        action={<Link href={`/platform/audit?tenantId=${tenant.id}`} className="text-sm text-primary hover:underline">{t("tenant.allAudit")}</Link>}
      >
        <AuditTable entries={audit} showTenant={false} />
      </Panel>
    </div>
  );
}
