import Link from "next/link";
import { getLocale, getTranslations } from "next-intl/server";
import { CalendarDays, ChevronRight } from "lucide-react";
import { requireMe } from "@konnyaku/session";
import { Input } from "@konnyaku/ui/components/ui/input";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@konnyaku/ui/components/ui/table";
import { EmptyState, PageHeader, Panel, StatusBadge } from "@konnyaku/ui/kk/blocks";
import { Field, NativeSelect } from "@konnyaku/ui/kk/field";
import { ActionForm, FormSheet } from "../action-form";
import { getAccess, getJson, localized, statusTone, type Branch, type ClassRow, type Program } from "../lib";

const deliveryModes = ["Online", "Offline", "Hybrid"] as const;

export default async function ClassesPage() {
  const { token } = await requireMe("staff");
  const t = await getTranslations("admin");
  const locale = await getLocale();
  const [classes, programs, branches, access] = await Promise.all([
    getJson<ClassRow[]>("/api/academic/classes", token, []),
    getJson<Program[]>("/api/academic/programs", token, []),
    getJson<Branch[]>("/api/organization/branches", token, []),
    getAccess(token),
  ]);
  const levels = programs.flatMap((p) => p.levels.map((level) => ({ ...level, program: p })));
  // New classes: active levels of active programs, in active branches the member runs.
  const openLevels = levels.filter((l) => l.isActive && l.program.isActive);
  const openBranches = branches.filter((b) => b.isActive && access.can("academic.classes", b.id));
  const levelName = new Map(levels.map((l) => [l.id, `${l.program.code} · ${localized(l.name, locale)}`]));
  const branchName = new Map(branches.map((b) => [b.id, localized(b.name, locale)]));

  return (
    <div className="space-y-6">
      <PageHeader
        title={t("classes.title")}
        description={t("classes.description")}
        actions={
          openBranches.length > 0 && (
          <FormSheet triggerLabel={t("classes.create")} title={t("classes.create")} description={t("classes.createHint")}>
            <ActionForm api="academic/classes" submitLabel={t("save")}>
              <Field label={t("fields.code")}><Input name="code" required /></Field>
              <Field label={t("classes.level")}>
                <NativeSelect name="courseLevelId" required>
                  {openLevels.map((l) => <option key={l.id} value={l.id}>{levelName.get(l.id)}</option>)}
                </NativeSelect>
              </Field>
              <Field label={t("classes.branch")}>
                <NativeSelect name="branchId" required>
                  {openBranches.map((b) => <option key={b.id} value={b.id}>{localized(b.name, locale)}</option>)}
                </NativeSelect>
              </Field>
              <div className="grid grid-cols-2 gap-3">
                <Field label={t("classes.mode")}>
                  <NativeSelect name="deliveryMode" required>
                    {deliveryModes.map((m) => <option key={m} value={m}>{t(`modes.${m}`)}</option>)}
                  </NativeSelect>
                </Field>
                <Field label={t("classes.capacity")}><Input name="capacity" type="number" min={1} defaultValue={12} required /></Field>
              </div>
              <Field label={t("classes.startsOn")}><Input name="startsOn" type="date" required /></Field>
            </ActionForm>
          </FormSheet>
          )
        }
      />
      <Panel bodyClassName="p-0">
        {classes.length === 0 ? (
          <div className="p-5"><EmptyState icon={CalendarDays} title={t("classes.empty")} /></div>
        ) : (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead className="pl-5">{t("fields.code")}</TableHead>
                <TableHead>{t("classes.level")}</TableHead>
                <TableHead className="hidden xl:table-cell">{t("classes.branch")}</TableHead>
                <TableHead className="w-44">{t("classes.seats")}</TableHead>
                <TableHead className="hidden lg:table-cell">{t("classes.startsOn")}</TableHead>
                <TableHead>{t("fields.status")}</TableHead>
                <TableHead className="pr-5" />
              </TableRow>
            </TableHeader>
            <TableBody>
              {classes.map((c) => (
                <TableRow key={c.id} className="group">
                  <TableCell className="pl-5">
                    <Link href={`/ops/classes/${c.id}`} className="font-medium hover:text-primary">{c.code}</Link>
                    <p className="text-xs text-muted-foreground">{t(`modes.${c.deliveryMode}`)}</p>
                  </TableCell>
                  <TableCell>{levelName.get(c.courseLevelId)}</TableCell>
                  <TableCell className="hidden xl:table-cell">{branchName.get(c.branchId)}</TableCell>
                  <TableCell>
                    <div className="space-y-1">
                      <span className="text-xs tabular-nums text-muted-foreground">
                        {t("classes.seatsValue", { enrolled: c.enrolledCount, capacity: c.capacity })}
                      </span>
                      <div className="h-1.5 overflow-hidden rounded-full bg-primary/15" role="presentation">
                        <div className="h-full rounded-full bg-primary" style={{ width: `${c.capacity ? Math.min(100, (c.enrolledCount / c.capacity) * 100) : 0}%` }} />
                      </div>
                    </div>
                  </TableCell>
                  <TableCell className="hidden tabular-nums lg:table-cell">{c.startsOn}</TableCell>
                  <TableCell><StatusBadge tone={statusTone[c.status]}>{t(`classStatus.${c.status}`)}</StatusBadge></TableCell>
                  <TableCell className="pr-5 text-right">
                    <Link href={`/ops/classes/${c.id}`} aria-label={t("classes.open", { code: c.code })} className="inline-grid size-8 place-items-center rounded-md text-muted-foreground hover:bg-accent hover:text-foreground">
                      <ChevronRight className="size-4 transition-transform group-hover:translate-x-0.5" />
                    </Link>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </Panel>
    </div>
  );
}
