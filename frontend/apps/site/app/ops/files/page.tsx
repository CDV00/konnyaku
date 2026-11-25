import { notFound } from "next/navigation";
import { getFormatter, getLocale, getTranslations } from "next-intl/server";
import { FileWarning, FolderOpen, HardDrive, Hourglass } from "lucide-react";
import { requireMe } from "@konnyaku/session";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@konnyaku/ui/components/ui/table";
import { EmptyState, PageHeader, Panel, StatCard, StatusBadge } from "@konnyaku/ui/kk/blocks";
import { ActionButton } from "../action-form";
import { emptyPage, getAccess, getJson, listQuery, statusTone, type Page, type SearchParams } from "../lib";
import { FilterSelect, ListBody, ListFrame, Pagination } from "../list";
import { DownloadButton, UploadButton } from "./file-actions";
import { formatBytes } from "./format";

const purposes = ["Material", "QuestionMedia", "Submission", "ChatAttachment", "Avatar"] as const;
const statuses = ["Ready", "Pending", "Quarantined", "Deleted"] as const;
type Purpose = (typeof purposes)[number];
type Status = (typeof statuses)[number];

type StoredFile = {
  id: string;
  ownerUserId: string;
  ownerKind: "Staff" | "Learner";
  purpose: Purpose;
  fileName: string;
  contentType: string;
  size: number;
  status: Status;
  createdAt: string;
};

type Usage = {
  limitBytes: number;
  usedBytes: number;
  pendingBytes: number;
  quarantinedCount: number;
  byPurpose: { purpose: Purpose; count: number; bytes: number }[];
};

/** Files of the center: storage used against the quota, every upload, quarantined files. Center-wide administrators. */
export default async function FilesPage({ searchParams }: { searchParams: SearchParams }) {
  const { token } = await requireMe("staff");
  const access = await getAccess(token);
  if (!access.can("content.files")) notFound();
  const t = await getTranslations("files");
  const format = await getFormatter();
  const locale = await getLocale();
  const query = await listQuery(searchParams, ["purpose", "status"]);
  const [usage, page] = await Promise.all([
    getJson<Usage | null>("/api/content/usage", token, null),
    getJson<Page<StoredFile>>(`/api/content/files?${query.toString()}`, token, emptyPage()),
  ]);
  const bytes = (value: number) => formatBytes(value, locale);
  const percent = usage && usage.limitBytes > 0 ? Math.min(100, (usage.usedBytes / usage.limitBytes) * 100) : 0;
  const level = percent >= 90 ? "destructive" : percent >= 75 ? "warning" : "primary";
  return (
    <div className="space-y-6">
      <PageHeader title={t("title")} description={t("description")} actions={<UploadButton />} />
      {usage && (
        <>
          <div className="grid gap-4 sm:grid-cols-3">
            <StatCard
              label={t("used")}
              value={bytes(usage.usedBytes)}
              hint={t("ofLimit", { limit: bytes(usage.limitBytes), percent: format.number(percent, { maximumFractionDigits: 1 }) })}
              icon={HardDrive}
              tone={level}
            />
            <StatCard label={t("pending")} value={bytes(usage.pendingBytes)} hint={t("pendingHint")} icon={Hourglass} tone="muted" />
            <StatCard
              label={t("quarantined")}
              count={usage.quarantinedCount}
              hint={t("quarantinedHint")}
              icon={FileWarning}
              tone={usage.quarantinedCount > 0 ? "destructive" : "muted"}
            />
          </div>
          <div
            className="h-2 overflow-hidden rounded-full bg-muted"
            role="progressbar"
            aria-label={t("used")}
            aria-valuenow={Math.round(percent)}
            aria-valuemin={0}
            aria-valuemax={100}
          >
            <div
              className={level === "destructive" ? "h-full bg-destructive" : level === "warning" ? "h-full bg-warning" : "h-full bg-primary"}
              style={{ width: `${percent}%` }}
            />
          </div>
          <Panel title={t("byPurpose")} bodyClassName="p-0">
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead className="pl-5">{t("colPurpose")}</TableHead>
                  <TableHead className="text-right">{t("colCount")}</TableHead>
                  <TableHead className="pr-5 text-right">{t("colSize")}</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {usage.byPurpose.map((p) => (
                  <TableRow key={p.purpose}>
                    <TableCell className="pl-5">{t(`purpose.${p.purpose}`)}</TableCell>
                    <TableCell className="text-right tabular-nums">{format.number(p.count)}</TableCell>
                    <TableCell className="pr-5 text-right tabular-nums">{bytes(p.bytes)}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </Panel>
        </>
      )}
      <ListFrame>
        <div className="flex flex-wrap gap-2">
          <FilterSelect
            name="purpose"
            label={t("colPurpose")}
            allLabel={t("allPurposes")}
            options={purposes.map((p) => ({ value: p, label: t(`purpose.${p}`) }))}
          />
          <FilterSelect
            name="status"
            label={t("colStatus")}
            allLabel={t("allStatuses")}
            options={statuses.map((s) => ({ value: s, label: t(`status.${s}`) }))}
          />
        </div>
        <ListBody>
          <Panel bodyClassName="p-0">
            {page.items.length === 0 ? (
              <div className="p-5"><EmptyState icon={FolderOpen} title={t("empty")} /></div>
            ) : (
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead className="pl-5">{t("colName")}</TableHead>
                    <TableHead className="hidden md:table-cell">{t("colPurpose")}</TableHead>
                    <TableHead className="hidden text-right sm:table-cell">{t("colSize")}</TableHead>
                    <TableHead>{t("colStatus")}</TableHead>
                    <TableHead className="hidden lg:table-cell">{t("colUploaded")}</TableHead>
                    <TableHead className="pr-5 text-right">{t("colActions")}</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {page.items.map((f) => (
                    <TableRow key={f.id} data-file={f.fileName}>
                      <TableCell className="max-w-40 pl-5 align-top sm:max-w-72">
                        <p className="truncate font-medium" title={f.fileName}>{f.fileName}</p>
                        <p className="text-xs text-muted-foreground">{t(`owner.${f.ownerKind}`)}</p>
                        <p className="text-xs text-muted-foreground md:hidden">{t(`purpose.${f.purpose}`)}</p>
                        <p className="text-xs text-muted-foreground tabular-nums sm:hidden">{bytes(f.size)}</p>
                      </TableCell>
                      <TableCell className="hidden align-top md:table-cell">{t(`purpose.${f.purpose}`)}</TableCell>
                      <TableCell className="hidden text-right align-top tabular-nums sm:table-cell">{bytes(f.size)}</TableCell>
                      <TableCell className="align-top">
                        <StatusBadge tone={statusTone[f.status] ?? "muted"}>{t(`status.${f.status}`)}</StatusBadge>
                      </TableCell>
                      <TableCell className="hidden align-top text-muted-foreground tabular-nums lg:table-cell">
                        {format.dateTime(new Date(f.createdAt), { dateStyle: "short", timeStyle: "short" })}
                      </TableCell>
                      <TableCell className="pr-5 text-right align-top">
                        <div className="flex flex-wrap justify-end gap-2">
                          {f.status === "Ready" && <DownloadButton id={f.id} />}
                          {f.status !== "Deleted" && (
                            <ActionButton
                              api={`content/files/${f.id}`}
                              method="DELETE"
                              label={t("delete")}
                              variant="ghost"
                              confirm={t("deleteConfirm", { name: f.fileName })}
                            />
                          )}
                        </div>
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            )}
          </Panel>
        </ListBody>
        <Pagination total={page.total} page={page.pageNumber} pageSize={page.pageSize} />
      </ListFrame>
    </div>
  );
}
