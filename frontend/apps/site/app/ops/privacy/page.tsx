import Link from "next/link";
import { notFound } from "next/navigation";
import { getFormatter, getTranslations } from "next-intl/server";
import { FileSearch, ShieldCheck } from "lucide-react";
import { requireMe } from "@konnyaku/session";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@konnyaku/ui/components/ui/table";
import { Textarea } from "@konnyaku/ui/components/ui/textarea";
import { EmptyState, PageHeader, Panel, StatusBadge } from "@konnyaku/ui/kk/blocks";
import { Field, NativeSelect } from "@konnyaku/ui/kk/field";
import { cn } from "@konnyaku/ui/lib/utils";
import { ActionButton, ActionForm, FormSheet } from "../action-form";
import { emptyPage, getAccess, getJson, listQuery, type Page, type SearchParams } from "../lib";
import { FilterDate, FilterSelect, ListBody, ListFrame, Pagination } from "../list";
import { DownloadButton } from "../personal-data";
import { SubjectFields } from "./subject-fields";

type Request = {
  id: string;
  subjectType: "student" | "lead";
  subjectId: string;
  subjectName: string | null;
  type: string;
  status: string;
  note: string | null;
  receivedAt: string;
  dueAt: string;
  overdue: boolean;
  resolvedAt: string | null;
  resolvedByName: string | null;
  resolution: string | null;
};

type Access = {
  id: string;
  action: string;
  subjectType: string;
  subjectId: string | null;
  subjectName: string | null;
  purpose: string | null;
  filter: Record<string, string>;
  rowCount: number | null;
  actorName: string | null;
  ipAddress: string | null;
  occurredAt: string;
};

const requestTypes = ["Access", "Rectification", "Erasure", "ConsentWithdrawal"] as const;
const requestStatuses = ["Open", "Completed", "Rejected"] as const;
const actions = ["Reveal", "Export", "Print", "SubjectExport", "Anonymize", "RetentionAnonymize"] as const;
const tabs = ["requests", "log"] as const;

/**
 * Personal data (tenant admin): data subject requests with their deadline, and the log of every access to personal
 * data (shows, exports, prints, erasures). The policy page itself is on the public site.
 */
export default async function PrivacyPage({ searchParams }: { searchParams: SearchParams }) {
  const { token } = await requireMe("staff");
  const access = await getAccess(token);
  if (!access.can("crm.privacy")) notFound();
  const t = await getTranslations("admin");
  const format = await getFormatter();
  const tab = (await searchParams).tab === "log" ? "log" : "requests";
  const when = (iso: string) => format.dateTime(new Date(iso), { dateStyle: "short", timeStyle: "short" });

  return (
    <div className="space-y-6">
      <PageHeader
        title={t("privacy.title")}
        description={t("privacy.description")}
        actions={
          tab === "requests" && (
            <FormSheet triggerLabel={t("privacy.createRequest")} title={t("privacy.createRequest")} description={t("privacy.createHint")}>
              <ActionForm api="crm/privacy/requests" submitLabel={t("save")}>
                <SubjectFields />
                <Field label={t("privacy.requestType")}>
                  <NativeSelect name="type" required>
                    {requestTypes.map((type) => <option key={type} value={type}>{t(`privacy.types.${type}`)}</option>)}
                  </NativeSelect>
                </Field>
                <Field label={t("privacy.note")} hint={t("privacy.noteHint")}><Textarea name="note" rows={3} maxLength={1000} /></Field>
              </ActionForm>
            </FormSheet>
          )
        }
      />
      <nav className="flex gap-1 border-b" aria-label={t("privacy.title")}>
        {tabs.map((item) => (
          <Link
            key={item}
            href={item === "requests" ? "/ops/privacy" : "/ops/privacy?tab=log"}
            className={cn(
              "-mb-px border-b-2 px-3 py-2 text-sm font-medium",
              tab === item ? "border-primary text-foreground" : "border-transparent text-muted-foreground hover:text-foreground",
            )}
          >
            {t(`privacy.tabs.${item}`)}
          </Link>
        ))}
      </nav>
      {tab === "requests" ? <Requests searchParams={searchParams} token={token} when={when} /> : <AccessLog searchParams={searchParams} token={token} when={when} />}
    </div>
  );
}

async function Requests({ searchParams, token, when }: { searchParams: SearchParams; token: string; when: (iso: string) => string }) {
  const t = await getTranslations("admin");
  const query = await listQuery(searchParams, ["status", "type"]);
  const page = await getJson<Page<Request>>(`/api/crm/privacy/requests?${query.toString()}`, token, emptyPage());
  const kind = (r: Request) => (r.subjectType === "student" ? "students" : "leads");
  return (
    <ListFrame>
      <div className="flex flex-wrap gap-2">
        <FilterSelect name="status" label={t("fields.status")} allLabel={t("list.allStatuses")} options={requestStatuses.map((s) => ({ value: s, label: t(`privacy.statuses.${s}`) }))} />
        <FilterSelect name="type" label={t("privacy.requestType")} allLabel={t("privacy.allTypes")} options={requestTypes.map((s) => ({ value: s, label: t(`privacy.types.${s}`) }))} />
      </div>
      <ListBody>
        <Panel bodyClassName="p-0">
          {page.items.length === 0 ? (
            <div className="p-5"><EmptyState icon={ShieldCheck} title={t("privacy.noRequests")} description={t("privacy.noRequestsHint")} /></div>
          ) : (
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead className="pl-5">{t("privacy.subject")}</TableHead>
                  <TableHead>{t("privacy.requestType")}</TableHead>
                  <TableHead className="hidden md:table-cell">{t("privacy.received")}</TableHead>
                  <TableHead>{t("privacy.due")}</TableHead>
                  <TableHead>{t("fields.status")}</TableHead>
                  <TableHead className="pr-5" />
                </TableRow>
              </TableHeader>
              <TableBody>
                {page.items.map((r) => {
                  const open = r.status === "Open";
                  const erase = r.type === "Erasure" || r.type === "ConsentWithdrawal";
                  return (
                    <TableRow key={r.id}>
                      <TableCell className="pl-5 align-top">
                        <p className="font-medium">{r.subjectName ?? r.subjectId.slice(0, 8)}</p>
                        <p className="text-xs text-muted-foreground">{t(`privacy.subjects.${r.subjectType}`)}</p>
                        {r.note && <p className="mt-1 max-w-72 text-xs text-muted-foreground">{r.note}</p>}
                      </TableCell>
                      <TableCell className="align-top">{t(`privacy.types.${r.type}`)}</TableCell>
                      <TableCell className="hidden align-top text-sm text-muted-foreground tabular-nums md:table-cell">{when(r.receivedAt)}</TableCell>
                      <TableCell className="align-top text-sm tabular-nums">
                        {open ? (
                          <span className={r.overdue ? "font-medium text-destructive" : undefined}>
                            {when(r.dueAt)}
                            {r.overdue && <StatusBadge tone="destructive" className="ml-2">{t("privacy.overdue")}</StatusBadge>}
                          </span>
                        ) : (
                          <span className="text-muted-foreground">{r.resolvedAt ? when(r.resolvedAt) : null}</span>
                        )}
                      </TableCell>
                      <TableCell className="align-top">
                        <StatusBadge tone={open ? "warning" : r.status === "Completed" ? "success" : "muted"}>{t(`privacy.statuses.${r.status}`)}</StatusBadge>
                        {!open && (r.resolvedByName || r.resolution) && (
                          <p className="mt-1 max-w-56 text-xs text-muted-foreground">{[r.resolvedByName, r.resolution].filter(Boolean).join(" — ")}</p>
                        )}
                      </TableCell>
                      <TableCell className="pr-5 text-right align-top">
                        {open && (
                          <span className="inline-flex flex-wrap items-start justify-end gap-2">
                            <DownloadButton href={`/ops/api/export/privacy/${kind(r)}/${r.subjectId}`} label={t("privacy.exportData")} variant="ghost" />
                            {erase && (
                              <ActionButton
                                api={`crm/privacy/${kind(r)}/${r.subjectId}/anonymize`}
                                body={{ requestId: r.id }}
                                label={t("privacy.anonymize")}
                                variant="destructive"
                                confirm={t("privacy.anonymizeConfirm")}
                              />
                            )}
                            <FormSheet triggerLabel={t("privacy.resolve")} title={t("privacy.resolve")} description={t("privacy.resolveHint")} variant="outline" size="sm" icon={false}>
                              <ActionForm api={`crm/privacy/requests/${r.id}/resolve`} submitLabel={t("save")}>
                                <Field label={t("fields.status")}>
                                  <NativeSelect name="status" required>
                                    <option value="Completed">{t("privacy.statuses.Completed")}</option>
                                    <option value="Rejected">{t("privacy.statuses.Rejected")}</option>
                                  </NativeSelect>
                                </Field>
                                <Field label={t("privacy.resolution")} hint={t("privacy.resolutionHint")}><Textarea name="resolution" rows={3} maxLength={1000} /></Field>
                              </ActionForm>
                            </FormSheet>
                          </span>
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
      <Pagination total={page.total} page={page.pageNumber} pageSize={page.pageSize} />
    </ListFrame>
  );
}

async function AccessLog({ searchParams, token, when }: { searchParams: SearchParams; token: string; when: (iso: string) => string }) {
  const t = await getTranslations("admin");
  const query = await listQuery(searchParams, ["action", "from", "to", "subject", "actor"]);
  const page = await getJson<Page<Access>>(`/api/crm/privacy/access-log?${query.toString()}`, token, emptyPage());
  return (
    <ListFrame>
      <div className="flex flex-wrap items-center gap-2">
        <FilterSelect name="action" label={t("privacy.action")} allLabel={t("privacy.allActions")} options={actions.map((a) => ({ value: a, label: t(`privacy.actions.${a}`) }))} />
        <FilterDate name="from" label={t("list.from")} />
        <FilterDate name="to" label={t("list.to")} />
      </div>
      <ListBody>
        <Panel bodyClassName="p-0">
          {page.items.length === 0 ? (
            <div className="p-5"><EmptyState icon={FileSearch} title={t("privacy.noAccess")} /></div>
          ) : (
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead className="pl-5">{t("privacy.when")}</TableHead>
                  <TableHead>{t("privacy.actor")}</TableHead>
                  <TableHead>{t("privacy.action")}</TableHead>
                  <TableHead>{t("privacy.subject")}</TableHead>
                  <TableHead className="hidden lg:table-cell">{t("privacy.details")}</TableHead>
                  <TableHead className="hidden xl:table-cell pr-5">{t("privacy.ip")}</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {page.items.map((a) => (
                  <TableRow key={a.id}>
                    <TableCell className="pl-5 text-sm text-muted-foreground tabular-nums">{when(a.occurredAt)}</TableCell>
                    <TableCell>{a.actorName ?? <span className="text-muted-foreground">{t("history.system")}</span>}</TableCell>
                    <TableCell>
                      <StatusBadge tone={a.action === "Export" || a.action === "SubjectExport" ? "warning" : a.action.includes("Anonymize") ? "destructive" : "muted"}>
                        {t(`privacy.actions.${a.action}`)}
                      </StatusBadge>
                    </TableCell>
                    <TableCell>
                      {a.subjectId ? (
                        <span>{a.subjectName ?? a.subjectId.slice(0, 8)}</span>
                      ) : (
                        <span className="text-muted-foreground">{t(`privacy.lists.${a.subjectType}`)}</span>
                      )}
                    </TableCell>
                    <TableCell className="hidden text-xs text-muted-foreground lg:table-cell">
                      {[
                        a.purpose ? (t.has(`privacy.purposes.${a.purpose}`) ? t(`privacy.purposes.${a.purpose}`) : t("privacy.retention", { period: a.purpose })) : null,
                        a.rowCount !== null ? t("privacy.rows", { count: a.rowCount }) : null,
                        Object.entries(a.filter).map(([k, v]) => `${k}=${v}`).join(", ") || null,
                      ].filter(Boolean).join(" · ")}
                    </TableCell>
                    <TableCell className="hidden pr-5 font-mono text-xs text-muted-foreground xl:table-cell">{a.ipAddress}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
        </Panel>
      </ListBody>
      <Pagination total={page.total} page={page.pageNumber} pageSize={page.pageSize} />
    </ListFrame>
  );
}
