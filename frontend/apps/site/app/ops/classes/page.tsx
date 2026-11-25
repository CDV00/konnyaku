import Link from "next/link";
import { getLocale, getTranslations } from "next-intl/server";
import { CalendarDays, ChevronRight, SearchX } from "lucide-react";
import { requireMe } from "@konnyaku/session";
import { Input } from "@konnyaku/ui/components/ui/input";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@konnyaku/ui/components/ui/table";
import { EmptyState, PageHeader, Panel, StatusBadge } from "@konnyaku/ui/kk/blocks";
import { Field, NativeSelect } from "@konnyaku/ui/kk/field";
import { ActionForm, FormSheet } from "../action-form";
import { classFilters } from "../filters";
import { emptyPage, getAccess, getJson, listQuery, localized, statusTone, type Branch, type ClassRow, type Localized, type Page, type Program, type SearchParams } from "../lib";
import { FilterDate, FilterSelect, ListBody, ListFrame, ListToolbar, Pagination, SortHeader } from "../list";

const deliveryModes = ["Online", "Offline", "Hybrid"] as const;
const bookingModes = ["Fixed", "Flexible"] as const;
const classStatuses = ["Planned", "Open", "Running", "Completed", "Cancelled"] as const;

export default async function ClassesPage({ searchParams }: { searchParams: SearchParams }) {
  const { token } = await requireMe("staff");
  const t = await getTranslations("admin");
  const locale = await getLocale();
  // Default: the classes still going (planned, open, running); "all" shows finished ones too.
  const query = await listQuery(searchParams, classFilters, { status: "current" });
  const [page, programs, branches, languages, access] = await Promise.all([
    getJson<Page<ClassRow>>(`/api/academic/classes?${query.toString()}`, token, emptyPage()),
    getJson<Program[]>("/api/academic/programs", token, []),
    getJson<Branch[]>("/api/organization/branches", token, []),
    getJson<{ code: string; name: Localized }[]>("/api/catalog/languages", token, []),
    getAccess(token),
  ]);
  const classes = page.items;
  const filtered = classFilters.some((key) => key !== "status" && query.has(key)) || query.get("status") !== "current";
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
              <Field label={t("classes.bookingMode")} hint={t("classes.bookingModeHint")}>
                <NativeSelect name="bookingMode" defaultValue="Fixed">
                  {bookingModes.map((m) => <option key={m} value={m}>{t(`bookingModes.${m}`)}</option>)}
                </NativeSelect>
              </Field>
            </ActionForm>
          </FormSheet>
          )
        }
      />
      <ListFrame>
      <ListToolbar placeholder={t("classes.search")}>
        <FilterSelect
          name="status"
          label={t("fields.status")}
          fallback="current"
          options={[
            { value: "current", label: t("classes.current") },
            ...classStatuses.map((s) => ({ value: s, label: t(`classStatus.${s}`) })),
            { value: "all", label: t("list.allStatuses") },
          ]}
        />
        <FilterSelect name="branch" label={t("classes.branch")} allLabel={t("list.allBranches")} options={branches.map((b) => ({ value: b.id, label: localized(b.name, locale) }))} />
        <FilterSelect name="language" label={t("programs.language")} allLabel={t("list.allLanguages")} options={languages.map((l) => ({ value: l.code, label: localized(l.name, locale) }))} />
        <FilterSelect name="mode" label={t("classes.mode")} allLabel={t("list.allModes")} options={deliveryModes.map((m) => ({ value: m, label: t(`modes.${m}`) }))} />
        <FilterDate name="startsFrom" label={t("list.startsFrom")} />
        <FilterDate name="startsTo" label={t("list.to")} />
      </ListToolbar>
      <ListBody>
      <Panel bodyClassName="p-0">
        {classes.length === 0 ? (
          <div className="p-5">{filtered ? <EmptyState icon={SearchX} title={t("list.noResults")} /> : <EmptyState icon={CalendarDays} title={t("classes.empty")} />}</div>
        ) : (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead className="pl-5"><SortHeader field="code" label={t("fields.code")} defaultField="startsOn" /></TableHead>
                <TableHead>{t("classes.level")}</TableHead>
                <TableHead className="hidden xl:table-cell">{t("classes.branch")}</TableHead>
                <TableHead className="w-44"><SortHeader field="enrolled" label={t("classes.seats")} defaultField="startsOn" /></TableHead>
                <TableHead className="hidden lg:table-cell"><SortHeader field="startsOn" label={t("classes.startsOn")} defaultField="startsOn" /></TableHead>
                <TableHead><SortHeader field="status" label={t("fields.status")} defaultField="startsOn" /></TableHead>
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
      </ListBody>
      <Pagination total={page.total} page={page.pageNumber} pageSize={page.pageSize} />
      </ListFrame>
    </div>
  );
}
