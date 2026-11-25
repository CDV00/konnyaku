import Link from "next/link";
import { getFormatter, getTranslations } from "next-intl/server";
import { ScrollText } from "lucide-react";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@konnyaku/ui/components/ui/table";
import { EmptyState } from "@konnyaku/ui/kk/blocks";
import type { AuditEntry } from "./lib";

/** Audit entries, newest first: when, who, what (translated action code), which center, details. */
export async function AuditTable({ entries, showTenant = true }: { entries: AuditEntry[]; showTenant?: boolean }) {
  const t = await getTranslations("platform.audit");
  const format = await getFormatter();
  if (entries.length === 0) return <div className="p-5"><EmptyState icon={ScrollText} title={t("empty")} /></div>;

  const action = (code: string) => {
    const key = `actions.${code.replaceAll(".", "_")}`;
    return t.has(key) ? t(key as "actions.tenant_create") : code;
  };
  const detailLabel = (key: string) => (t.has(`detail.${key}`) ? t(`detail.${key}` as "detail.email") : key);

  return (
    <Table>
      <TableHeader>
        <TableRow>
          <TableHead className="pl-5">{t("columns.at")}</TableHead>
          <TableHead>{t("columns.action")}</TableHead>
          <TableHead className="hidden md:table-cell">{t("columns.actor")}</TableHead>
          {showTenant && <TableHead className="hidden lg:table-cell">{t("columns.tenant")}</TableHead>}
          <TableHead className="hidden xl:table-cell">{t("columns.details")}</TableHead>
          <TableHead className="hidden pr-5 xl:table-cell">{t("columns.ip")}</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {entries.map((entry) => {
          const details = Object.entries(entry.details).filter(([, value]) => value);
          return (
            <TableRow key={entry.id}>
              <TableCell className="pl-5 whitespace-nowrap text-muted-foreground tabular-nums">
                {format.dateTime(new Date(entry.at), { dateStyle: "short", timeStyle: "medium" })}
              </TableCell>
              <TableCell>
                <span className="font-medium">{action(entry.action)}</span>
                <span className="block text-xs text-muted-foreground md:hidden">{entry.actor === "automation" ? t("automation") : entry.actor}</span>
              </TableCell>
              <TableCell className="hidden md:table-cell">{entry.actor === "automation" ? t("automation") : entry.actor}</TableCell>
              {showTenant && (
                <TableCell className="hidden lg:table-cell">
                  {entry.tenantId && (
                    <Link href={`/platform/tenants/${entry.tenantId}`} className="hover:text-primary hover:underline">
                      {entry.tenantName ?? entry.tenantId}
                    </Link>
                  )}
                </TableCell>
              )}
              <TableCell className="hidden text-xs text-muted-foreground xl:table-cell">
                {details.map(([key, value]) => (
                  <span key={key} className="block">{t("detailLine", { label: detailLabel(key), value: value ?? "" })}</span>
                ))}
              </TableCell>
              <TableCell className="hidden pr-5 font-mono text-xs text-muted-foreground xl:table-cell">{entry.ipAddress}</TableCell>
            </TableRow>
          );
        })}
      </TableBody>
    </Table>
  );
}
