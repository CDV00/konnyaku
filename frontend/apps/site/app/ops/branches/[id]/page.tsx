import Link from "next/link";
import { notFound } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { ArrowLeft, DoorOpen } from "lucide-react";
import { requireMe } from "@konnyaku/session";
import { Input } from "@konnyaku/ui/components/ui/input";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@konnyaku/ui/components/ui/table";
import { EmptyState, PageHeader, Panel, StatusBadge } from "@konnyaku/ui/kk/blocks";
import { Field } from "@konnyaku/ui/kk/field";
import { ActionButton, ActionForm, FormSheet } from "../../action-form";
import { ChangeHistory } from "../../history";
import type { HistoryEntry } from "../../history-entry";
import { getAccess, getJson, holidayWindow, localized, type Branch, type Holiday, type Room } from "../../lib";
import { HolidaysPanel } from "../holidays";

export default async function BranchPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const { token } = await requireMe("staff");
  const t = await getTranslations("admin");
  const locale = await getLocale();
  const { from, to } = holidayWindow();
  const [branches, rooms, holidays, access, history] = await Promise.all([
    getJson<Branch[]>("/api/organization/branches", token, []),
    getJson<Room[]>(`/api/organization/rooms?branchId=${id}`, token, []),
    getJson<Holiday[]>(`/api/organization/holidays?from=${from}&to=${to}`, token, []),
    getAccess(token),
    getJson<HistoryEntry[]>(`/api/organization/history/${id}`, token, []),
  ]);
  const branch = branches.find((b) => b.id === id);
  if (!branch) notFound();
  const canRooms = access.can("organization.rooms", branch.id);
  const names = Object.fromEntries(branches.map((b) => [b.id, localized(b.name, locale)]));
  const roomFields = (room?: Room) => (
    <>
      <Field label={t("fields.code")}><Input name="code" required defaultValue={room?.code} /></Field>
      <Field label={t("rooms.capacity")}><Input name="capacity" type="number" min={1} max={1000} required defaultValue={room?.capacity ?? 12} /></Field>
    </>
  );

  return (
    <div className="space-y-6">
      <Link href="/ops/branches" className="inline-flex items-center gap-1.5 text-sm text-muted-foreground hover:text-foreground">
        <ArrowLeft className="size-4" />
        {t("branches.back")}
      </Link>
      <PageHeader
        title={
          <span className="flex flex-wrap items-center gap-3">
            {localized(branch.name, locale)}
            {!branch.isActive && <StatusBadge tone="muted">{t("inactive")}</StatusBadge>}
          </span>
        }
        description={t("branches.detailHint", { code: branch.code, timeZone: branch.timeZone })}
      />
      <div className="grid gap-6 lg:grid-cols-2">
        <Panel
          title={t("rooms.title")}
          action={
            canRooms && branch.isActive && (
              <FormSheet triggerLabel={t("rooms.create")} title={t("rooms.create")} variant="outline" size="sm">
                <ActionForm api={`organization/branches/${branch.id}/rooms`} submitLabel={t("save")}>{roomFields()}</ActionForm>
              </FormSheet>
            )
          }
          bodyClassName="p-0"
        >
          {rooms.length === 0 ? (
            <div className="p-5"><EmptyState icon={DoorOpen} title={t("rooms.empty")} /></div>
          ) : (
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead className="pl-5">{t("fields.code")}</TableHead>
                  <TableHead className="text-right">{t("rooms.capacity")}</TableHead>
                  <TableHead>{t("fields.status")}</TableHead>
                  <TableHead className="pr-5" />
                </TableRow>
              </TableHeader>
              <TableBody>
                {rooms.map((r) => (
                  <TableRow key={r.id}>
                    <TableCell className="pl-5 font-mono text-xs">{r.code}</TableCell>
                    <TableCell className="text-right tabular-nums">{r.capacity}</TableCell>
                    <TableCell>
                      <StatusBadge tone={r.isActive ? "success" : "muted"}>{r.isActive ? t("active") : t("inactive")}</StatusBadge>
                    </TableCell>
                    <TableCell className="pr-5 text-right">
                      {canRooms && (
                        <span className="inline-flex flex-wrap items-start justify-end gap-2">
                          <FormSheet triggerLabel={t("edit")} title={t("rooms.edit", { code: r.code })} variant="ghost" size="sm" icon="edit">
                            <ActionForm api={`organization/rooms/${r.id}`} method="PUT" submitLabel={t("save")}>{roomFields(r)}</ActionForm>
                            <section className="mt-6 space-y-3">
                              <h3 className="text-sm font-semibold">{t("history.title")}</h3>
                              <ChangeHistory sources={[{ module: "organization", id: r.id }]} names={names} />
                            </section>
                          </FormSheet>
                          {r.isActive ? (
                            <ActionButton api={`organization/rooms/${r.id}/deactivate`} label={t("deactivate")} variant="ghost" confirm={t("rooms.deactivateConfirm")} />
                          ) : (
                            <ActionButton api={`organization/rooms/${r.id}/activate`} label={t("activate")} variant="ghost" />
                          )}
                        </span>
                      )}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
        </Panel>
        <HolidaysPanel
          holidays={holidays.filter((h) => h.branchId === branch.id)}
          branchId={branch.id}
          canEdit={access.can("organization.holidays", branch.id)}
          locale={locale}
          names={names}
        />
      </div>
      <Panel title={t("history.title")}>
        <ChangeHistory entries={history} names={names} />
      </Panel>
    </div>
  );
}
