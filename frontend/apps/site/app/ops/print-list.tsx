import { getFormatter, getLocale, getTranslations } from "next-intl/server";
import { apiFetch, requireMe } from "@konnyaku/session";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@konnyaku/ui/components/ui/table";
import { FormMessage } from "@konnyaku/ui/kk/blocks";
import { getJson, listQuery, localized, type Branch, type SearchParams } from "./lib";
import { PrintOnLoad } from "./personal-data";

type PrintRow = { id: string; name: string; birthYear: string | null; phone: string | null; email: string | null; branchId: string | null; status: string };
type PrintView = { rows: PrintRow[]; total: number; printedBy: string; printedAt: string };

/**
 * Printable list (students or leads) as filtered on screen. The API logs the print, applies the row limit and returns
 * contact details masked plus the stamp (operator masked, server time) printed under every page.
 */
export async function PrintList({ kind, filters, searchParams }: { kind: "students" | "leads"; filters: readonly string[]; searchParams: SearchParams }) {
  const { token } = await requireMe("staff");
  const t = await getTranslations("admin");
  const format = await getFormatter();
  const locale = await getLocale();
  const query = await listQuery(searchParams, filters);
  query.delete("page");
  query.delete("pageSize");
  const [response, branches] = await Promise.all([
    apiFetch(`/api/crm/${kind}/print?${query.toString()}`, { token }),
    getJson<Branch[]>("/api/organization/branches", token, []),
  ]);
  if (!response.ok) {
    const error = (await response.json().catch(() => ({}))) as { code?: string; params?: Record<string, string | number> };
    const code = error.code ?? (response.status === 403 ? "forbidden" : "unknown");
    const key = `errors.${code.replaceAll(".", "_")}`;
    return <FormMessage>{t.has(key) ? t(key, error.params ?? {}) : t("errors.unknown", { code })}</FormMessage>;
  }
  const view = (await response.json()) as PrintView;
  const branchName = new Map(branches.map((b) => [b.id, localized(b.name, locale)]));
  const stamp = t("privacy.printStamp", {
    name: view.printedBy,
    when: format.dateTime(new Date(view.printedAt), { dateStyle: "short", timeStyle: "short" }),
    count: view.total,
  });
  return (
    <div className="space-y-4 print:text-xs">
      <PrintOnLoad />
      <header className="flex items-baseline justify-between gap-4">
        <h1 className="text-xl font-semibold">{t(kind === "students" ? "privacy.printStudents" : "privacy.printLeads")}</h1>
        <span className="text-sm text-muted-foreground">{t("privacy.printCount", { count: view.total })}</span>
      </header>
      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>{t("students.fullName")}</TableHead>
            <TableHead>{t("students.birthYear")}</TableHead>
            <TableHead>{t("fields.phone")}</TableHead>
            <TableHead>{t("fields.email")}</TableHead>
            <TableHead>{t("classes.branch")}</TableHead>
            <TableHead>{t("fields.status")}</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {view.rows.map((row) => (
            <TableRow key={row.id}>
              <TableCell className="font-medium">{row.name}</TableCell>
              <TableCell className="font-mono">{row.birthYear}</TableCell>
              <TableCell className="font-mono">{row.phone}</TableCell>
              <TableCell className="font-mono">{row.email}</TableCell>
              <TableCell>{row.branchId ? branchName.get(row.branchId) : null}</TableCell>
              <TableCell>{kind === "leads" ? t(`leadStatus.${row.status}`) : t(row.status === "Active" ? "active" : "inactive")}</TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
      {/* Repeated at the bottom of every printed page. */}
      <footer className="border-t pt-2 text-xs text-muted-foreground print:fixed print:inset-x-0 print:bottom-0 print:bg-background">{stamp}</footer>
    </div>
  );
}
