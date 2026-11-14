import { getLocale, getTranslations } from "next-intl/server";
import { requireMe } from "@konnyaku/session";
import { ActionForm } from "../action-form";
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

  return (
    <main>
      <h1>{t("programs.title")}</h1>
      {programs.length === 0 && <p className="muted">{t("programs.empty")}</p>}
      {programs.map((program) => {
        const framework = frameworkOf.get(program.frameworkCode);
        return (
          <section key={program.id}>
            <h2>
              {t("programs.heading", {
                code: program.code,
                name: localized(program.name, locale),
                language: program.languageCode,
                framework: program.frameworkCode,
              })}
              {program.isPublic && <small className="muted">{t("programs.publicTag")}</small>}
            </h2>
            {program.levels.length === 0 ? (
              <p className="muted">{t("programs.noLevels")}</p>
            ) : (
              <table>
                <thead>
                  <tr>
                    <th>{t("fields.code")}</th>
                    <th>{t("fields.name")}</th>
                    <th>{t("programs.target")}</th>
                    <th className="num">{t("programs.sessionCount")}</th>
                    <th className="num">{t("programs.sessionMinutes")}</th>
                  </tr>
                </thead>
                <tbody>
                  {program.levels.map((level) => (
                    <tr key={level.id}>
                      <td>{level.code}</td>
                      <td>{localized(level.name, locale)}</td>
                      <td>{level.targetLevelCode}</td>
                      <td className="num">{level.sessionCount}</td>
                      <td className="num">{level.sessionMinutes}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
            <details className="panel">
              <summary>{t("programs.addLevel")}</summary>
              <ActionForm api={`academic/programs/${program.id}/levels`} submitLabel={t("save")}>
                <label>{t("fields.code")}<input name="code" required className="compact" /></label>
                <label>{t("fields.nameVi")}<input name="name.vi" required /></label>
                <label>{t("fields.nameEn")}<input name="name.en" /></label>
                <label>
                  {t("programs.target")}
                  <select name="targetLevelCode" required>
                    {framework?.levels.map((level) => <option key={level.code} value={level.code}>{level.code}</option>)}
                  </select>
                </label>
                <label>{t("programs.sessionCount")}<input name="sessionCount" type="number" min={1} required className="compact" /></label>
                <label>{t("programs.sessionMinutes")}<input name="sessionMinutes" type="number" min={15} step={15} defaultValue={90} required className="compact" /></label>
              </ActionForm>
            </details>
          </section>
        );
      })}
      <details className="panel">
        <summary>{t("programs.create")}</summary>
        <ActionForm api="academic/programs" submitLabel={t("save")}>
          <label>{t("fields.code")}<input name="code" required className="compact" /></label>
          <label>{t("fields.nameVi")}<input name="name.vi" required /></label>
          <label>{t("fields.nameEn")}<input name="name.en" /></label>
          <label>
            {t("programs.language")}
            <select name="languageCode" required>
              {languages.map((l) => <option key={l.code} value={l.code}>{localized(l.name, locale)}</option>)}
            </select>
          </label>
          <label>
            {t("programs.framework")}
            <select name="frameworkCode" required>
              {frameworks.map((f) => (
                <option key={f.code} value={f.code}>
                  {t("programs.frameworkOption", { name: localized(f.name, locale), language: f.languageCode ?? t("programs.shared") })}
                </option>
              ))}
            </select>
          </label>
          <label className="inline"><input name="isPublic" type="checkbox" defaultChecked />{t("programs.public")}</label>
        </ActionForm>
      </details>
    </main>
  );
}
