import { getLocale, getTranslations } from "next-intl/server";
import { Globe, Layers } from "lucide-react";
import { requireMe } from "@konnyaku/session";
import { Input } from "@konnyaku/ui/components/ui/input";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@konnyaku/ui/components/ui/table";
import { EmptyState, PageHeader, Panel, StatusBadge } from "@konnyaku/ui/kk/blocks";
import { CheckRow, Field, NativeSelect } from "@konnyaku/ui/kk/field";
import { ActionButton, ActionForm, FormSheet } from "../action-form";
import { ChangeHistory } from "../history";
import { getAccess, getJson, localized, type Level, type Localized, type Program } from "../lib";

type Language = { code: string; name: Localized };
type Framework = { code: string; name: Localized; languageCode: string | null; levels: { code: string; ordinal: number }[] };

export default async function ProgramsPage() {
  const { token } = await requireMe("staff");
  const t = await getTranslations("admin");
  const locale = await getLocale();
  const [programs, languages, frameworks, access] = await Promise.all([
    getJson<Program[]>("/api/academic/programs", token, []),
    getJson<Language[]>("/api/catalog/languages", token, []),
    getJson<Framework[]>("/api/catalog/frameworks", token, []),
    getAccess(token),
  ]);
  const frameworkOf = new Map(frameworks.map((f) => [f.code, f]));
  const languageName = new Map(languages.map((l) => [l.code, localized(l.name, locale)]));
  const canManage = access.can("academic.programs");

  const programFields = (program?: Program) => {
    const locked = !!program && program.levels.length > 0;
    return (
      <>
        <Field label={t("fields.code")}><Input name="code" required defaultValue={program?.code} /></Field>
        <Field label={t("fields.nameVi")}><Input name="name.vi" required defaultValue={program?.name.vi} /></Field>
        <Field label={t("fields.nameEn")}><Input name="name.en" defaultValue={program?.name.en} /></Field>
        <Field label={t("programs.language")} hint={locked ? t("programs.lockedHint") : undefined}>
          {locked && <input type="hidden" name="languageCode" value={program.languageCode} />}
          <NativeSelect name={locked ? undefined : "languageCode"} required disabled={locked} defaultValue={program?.languageCode}>
            {languages.map((l) => <option key={l.code} value={l.code}>{localized(l.name, locale)}</option>)}
          </NativeSelect>
        </Field>
        <Field label={t("programs.framework")}>
          {locked && <input type="hidden" name="frameworkCode" value={program.frameworkCode} />}
          <NativeSelect name={locked ? undefined : "frameworkCode"} required disabled={locked} defaultValue={program?.frameworkCode}>
            {frameworks.map((f) => (
              <option key={f.code} value={f.code}>
                {t("programs.frameworkOption", { name: localized(f.name, locale), language: f.languageCode ?? t("programs.shared") })}
              </option>
            ))}
          </NativeSelect>
        </Field>
        {program && (
          <div className="grid grid-cols-2 gap-3">
            <Field label={t("programs.ageGroup")}><Input name="ageGroup" defaultValue={program.ageGroup ?? ""} /></Field>
            <Field label={t("programs.goal")}><Input name="goal" defaultValue={program.goal ?? ""} /></Field>
          </div>
        )}
        <CheckRow name="isPublic" defaultChecked={program?.isPublic ?? true}>{t("programs.public")}</CheckRow>
      </>
    );
  };

  const levelFields = (framework: Framework | undefined, level?: Level) => (
    <>
      <Field label={t("fields.code")}><Input name="code" required defaultValue={level?.code} /></Field>
      <Field label={t("fields.nameVi")}><Input name="name.vi" required defaultValue={level?.name.vi} /></Field>
      <Field label={t("fields.nameEn")}><Input name="name.en" defaultValue={level?.name.en} /></Field>
      <Field label={t("programs.target")}>
        <NativeSelect name="targetLevelCode" required defaultValue={level?.targetLevelCode}>
          {framework?.levels.map((l) => <option key={l.code} value={l.code}>{l.code}</option>)}
        </NativeSelect>
      </Field>
      <div className="grid grid-cols-2 gap-3">
        <Field label={t("programs.sessionCount")} hint={level ? t("programs.sessionCountHint") : undefined}>
          <Input name="sessionCount" type="number" min={1} required defaultValue={level?.sessionCount} />
        </Field>
        <Field label={t("programs.sessionMinutes")} hint={level ? t("programs.sessionMinutesHint") : undefined}>
          <Input name="sessionMinutes" type="number" min={15} step={15} defaultValue={level?.sessionMinutes ?? 90} required />
        </Field>
      </div>
    </>
  );

  return (
    <div className="space-y-6">
      <PageHeader
        title={t("programs.title")}
        description={t("programs.description")}
        actions={
          canManage && (
          <FormSheet triggerLabel={t("programs.create")} title={t("programs.create")}>
            <ActionForm api="academic/programs" submitLabel={t("save")}>
              {programFields()}
            </ActionForm>
          </FormSheet>
          )
        }
      />
      {programs.length === 0 && <EmptyState icon={Layers} title={t("programs.empty")} />}
      {programs.map((program) => {
        const framework = frameworkOf.get(program.frameworkCode);
        return (
          <Panel
            key={program.id}
            title={
              <span className="flex flex-wrap items-center gap-2">
                <span className="rounded bg-muted px-2 py-0.5 font-mono text-xs">{program.code}</span>
                {localized(program.name, locale)}
                {program.isPublic && program.isActive && (
                  <StatusBadge tone="success">
                    <Globe className="size-3" />
                    {t("programs.publicTag")}
                  </StatusBadge>
                )}
                {!program.isActive && <StatusBadge tone="muted">{t("inactive")}</StatusBadge>}
              </span>
            }
            description={t("programs.meta", {
              language: languageName.get(program.languageCode) ?? program.languageCode,
              framework: framework ? localized(framework.name, locale) : program.frameworkCode,
            })}
            action={
              canManage && (
                <span className="flex flex-wrap items-start gap-2">
                  {program.isActive && (
                    <FormSheet triggerLabel={t("programs.addLevel")} title={t("programs.addLevel")} description={localized(program.name, locale)} variant="outline" size="sm">
                      <ActionForm api={`academic/programs/${program.id}/levels`} submitLabel={t("save")}>{levelFields(framework)}</ActionForm>
                    </FormSheet>
                  )}
                  <FormSheet triggerLabel={t("edit")} title={t("programs.edit", { code: program.code })} variant="ghost" size="sm" icon="edit">
                    <ActionForm api={`academic/programs/${program.id}`} method="PUT" submitLabel={t("save")}>{programFields(program)}</ActionForm>
                    <section className="mt-6 space-y-3">
                      <h3 className="text-sm font-semibold">{t("history.title")}</h3>
                      <ChangeHistory sources={[{ module: "academic", id: program.id }]} />
                    </section>
                  </FormSheet>
                  {program.isActive ? (
                    <ActionButton api={`academic/programs/${program.id}/deactivate`} label={t("deactivate")} variant="ghost" confirm={t("programs.deactivateConfirm")} />
                  ) : (
                    <ActionButton api={`academic/programs/${program.id}/activate`} label={t("activate")} variant="ghost" />
                  )}
                </span>
              )
            }
            bodyClassName="p-0"
          >
            {program.levels.length === 0 ? (
              <p className="px-5 py-4 text-sm text-muted-foreground">{t("programs.noLevels")}</p>
            ) : (
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead className="pl-5">{t("fields.code")}</TableHead>
                    <TableHead>{t("fields.name")}</TableHead>
                    <TableHead>{t("programs.target")}</TableHead>
                    <TableHead className="text-right">{t("programs.sessionCount")}</TableHead>
                    <TableHead className="text-right">{t("programs.sessionMinutes")}</TableHead>
                    <TableHead className="pr-5" />
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {program.levels.map((level) => (
                    <TableRow key={level.id} className={level.isActive ? "" : "opacity-60"}>
                      <TableCell className="pl-5 font-mono text-xs">{level.code}</TableCell>
                      <TableCell className="font-medium">
                        {localized(level.name, locale)}
                        {!level.isActive && <StatusBadge tone="muted" className="ml-2">{t("inactive")}</StatusBadge>}
                      </TableCell>
                      <TableCell><StatusBadge tone="primary">{level.targetLevelCode}</StatusBadge></TableCell>
                      <TableCell className="text-right tabular-nums">{level.sessionCount}</TableCell>
                      <TableCell className="text-right tabular-nums">{level.sessionMinutes}</TableCell>
                      <TableCell className="pr-5 text-right">
                        {canManage && (
                          <span className="inline-flex flex-wrap items-start justify-end gap-2">
                            <FormSheet triggerLabel={t("edit")} title={t("programs.editLevel", { code: level.code })} variant="ghost" size="sm" icon="edit">
                              <ActionForm api={`academic/levels/${level.id}`} method="PUT" submitLabel={t("save")}>{levelFields(framework, level)}</ActionForm>
                            </FormSheet>
                            {level.isActive ? (
                              <ActionButton api={`academic/levels/${level.id}/deactivate`} label={t("deactivate")} variant="ghost" confirm={t("programs.deactivateLevelConfirm")} />
                            ) : (
                              <ActionButton api={`academic/levels/${level.id}/activate`} label={t("activate")} variant="ghost" />
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
        );
      })}
    </div>
  );
}
