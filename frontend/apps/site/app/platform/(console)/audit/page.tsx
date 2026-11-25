import Link from "next/link";
import { getTranslations } from "next-intl/server";
import { ChevronRight } from "lucide-react";
import { PageHeader, Panel } from "@konnyaku/ui/kk/blocks";
import { AuditTable } from "../../audit-table";
import { consoleGet, requireActiveOperator, type AuditEntry } from "../../lib";

const pageSize = 100;

export default async function AuditPage({ searchParams }: { searchParams: Promise<{ before?: string; tenantId?: string }> }) {
  const { token } = await requireActiveOperator();
  const { before, tenantId } = await searchParams;
  const t = await getTranslations("platform.audit");
  const query = new URLSearchParams({ take: String(pageSize) });
  if (before) query.set("before", before);
  if (tenantId) query.set("tenantId", tenantId);
  const entries = await consoleGet<AuditEntry[]>(`audit?${query}`, token, []);
  const last = entries.at(-1);
  const older = new URLSearchParams(tenantId ? { tenantId } : {});
  if (last) older.set("before", last.at);

  return (
    <div className="space-y-6">
      <PageHeader title={t("title")} description={t("description")} />
      <Panel bodyClassName="p-0">
        <AuditTable entries={entries} />
      </Panel>
      <div className="flex justify-end gap-2 text-sm">
        {before && (
          <Link href={tenantId ? `/platform/audit?tenantId=${tenantId}` : "/platform/audit"} className="rounded-md border px-3 py-1.5 hover:bg-accent">
            {t("newest")}
          </Link>
        )}
        {entries.length === pageSize && (
          <Link href={`/platform/audit?${older}`} className="inline-flex items-center gap-1 rounded-md border px-3 py-1.5 hover:bg-accent">
            {t("older")}
            <ChevronRight className="size-4" />
          </Link>
        )}
      </div>
    </div>
  );
}
