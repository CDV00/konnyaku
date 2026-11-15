import { getLocale, getTranslations } from "next-intl/server";
import { Globe, Layers } from "lucide-react";
import { requireMe } from "@konnyaku/session";
import { Input } from "@konnyaku/ui/components/ui/input";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@konnyaku/ui/components/ui/table";
import { EmptyState, PageHeader, Panel, StatusBadge } from "@konnyaku/ui/kk/blocks";
import { CheckRow, Field, NativeSelect } from "@konnyaku/ui/kk/field";
import { ActionForm, FormSheet } from "../action-form";
import { getJson, localized, type Localized, type Program } from "../lib";

type Language = { code: string; name: Localized };
type Framework = { code: string; name: Localized; languageCode: string | null; levels: { code: string; ordinal: number }[] };

export default async function ProgramsPage() {
  const { token } = await requireMe("staff");
  const t = await getTranslations("admin");
  const locale = await getLocale();
  const [programs, languages, frameworks] = await Promise.all([
    getJson<Program[]>("/api/academic/programs", token, []),
    getJson<Language[]>("/api/catalog/languages", token, []),
    getJson<Framework[]>("/api/catalog/frameworks", token, []),
  ]);
  const frameworkOf = new Map(frameworks.map((f) => [f.code, f]));
  const languageName = new Map(languages.map((l) => [l.code, localized(l.name, locale)]));

  return (
    <div className="space-y-6">
      <PageHeader
        title={t("programs.title")}
        description={t("programs.description")}
        actions={
          <FormSheet triggerLabel={t("programs.create")} title={t("programs.create")}>
            <ActionForm api="academic/programs" submitLabel={t("save")}>
              <Field label={t("fields.code")}><Input name="code" required /></Field>
              <Field label={t("fields.nameVi")}><Input name="name.vi" required /></Field>
              <Field label={t("fields.nameEn")}><Input name="name.en" /></Field>
              <Field label={t("programs.language")}>
                <NativeSelect name="languageCode" required>
                  {languages.map((l) => <option key={l.code} value={l.code}>{localized(l.name, locale)}</option>)}
                </NativeSelect>
              </Field>
              <Field label={t("programs.framework")}>
                <NativeSelect name="frameworkCode" required>
                  {frameworks.map((f) => (
                    <option key={f.code} value={f.code}>
                      {t("programs.frameworkOption", { name: localized(f.name, locale), language: f.languageCode ?? t("programs.shared") })}
                    </option>
                  ))}
                </NativeSelect>
              </Field>
              <CheckRow name="isPublic" defaultChecked>{t("programs.public")}</CheckRow>
            </ActionForm>
          </FormSheet>
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
                {program.isPublic && (
                  <StatusBadge tone="success">
                    <Globe className="size-3" />
                    {t("programs.publicTag")}
                  </StatusBadge>
                )}
              </span>
            }
            description={t("programs.meta", {
              language: languageName.get(program.languageCode) ?? program.languageCode,
              framework: framework ? localized(framework.name, locale) : program.frameworkCode,
            })}
            action={
              <FormSheet triggerLabel={t("programs.addLevel")} title={t("programs.addLevel")} description={localized(program.name, locale)} variant="outline" size="sm">
                <ActionForm api={`academic/programs/${program.id}/levels`} submitLabel={t("save")}>
                  <Field label={t("fields.code")}><Input name="code" required /></Field>
                  <Field label={t("fields.nameVi")}><Input name="name.vi" required /></Field>
                  <Field label={t("fields.nameEn")}><Input name="name.en" /></Field>
                  <Field label={t("programs.target")}>
                    <NativeSelect name="targetLevelCode" required>
                      {framework?.levels.map((level) => <option key={level.code} value={level.code}>{level.code}</option>)}
                    </NativeSelect>
                  </Field>
                  <div className="grid grid-cols-2 gap-3">
                    <Field label={t("programs.sessionCount")}><Input name="sessionCount" type="number" min={1} required /></Field>
                    <Field label={t("programs.sessionMinutes")}>
                      <Input name="sessionMinutes" type="number" min={15} step={15} defaultValue={90} required />
                    </Field>
                  </div>
                </ActionForm>
              </FormSheet>
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
                    <TableHead className="pr-5 text-right">{t("programs.sessionMinutes")}</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {program.levels.map((level) => (
                    <TableRow key={level.id}>
                      <TableCell className="pl-5 font-mono text-xs">{level.code}</TableCell>
                      <TableCell className="font-medium">{localized(level.name, locale)}</TableCell>
                      <TableCell><StatusBadge tone="primary">{level.targetLevelCode}</StatusBadge></TableCell>
                      <TableCell className="text-right tabular-nums">{level.sessionCount}</TableCell>
                      <TableCell className="pr-5 text-right tabular-nums">{level.sessionMinutes}</TableCell>
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
