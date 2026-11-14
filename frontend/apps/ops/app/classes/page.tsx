import Link from "next/link";
import { getLocale, getTranslations } from "next-intl/server";
import { requireMe } from "@konnyaku/session";
import { ActionForm } from "../action-form";
import { getJson, localized, type Branch, type ClassRow, type Program } from "../lib";

const deliveryModes = ["Online", "Offline", "Hybrid"] as const;

export default async function ClassesPage() {
  const { token } = await requireMe("staff");
  const t = await getTranslations("admin");
  const locale = await getLocale();
  const [classes, programs, branches] = await Promise.all([
    getJson<ClassRow[]>("/api/academic/classes", token, []),
    getJson<Program[]>("/api/academic/programs", token, []),
    getJson<Branch[]>("/api/organization/branches", token, []),
  ]);
  const levels = programs.flatMap((p) => p.levels.map((level) => ({ ...level, program: p })));
  const levelName = new Map(levels.map((l) => [l.id, `${l.program.code} · ${localized(l.name, locale)}`]));
  const branchName = new Map(branches.map((b) => [b.id, localized(b.name, locale)]));

  return (
    <main>
      <h1>{t("classes.title")}</h1>
      {classes.length === 0 ? (
        <p className="muted">{t("classes.empty")}</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>{t("fields.code")}</th>
              <th>{t("classes.level")}</th>
              <th>{t("classes.branch")}</th>
              <th>{t("classes.mode")}</th>
              <th className="num">{t("classes.seats")}</th>
              <th>{t("classes.startsOn")}</th>
              <th>{t("fields.status")}</th>
            </tr>
          </thead>
          <tbody>
            {classes.map((c) => (
              <tr key={c.id}>
                <td><Link href={`/classes/${c.id}`}>{c.code}</Link></td>
                <td>{levelName.get(c.courseLevelId)}</td>
                <td>{branchName.get(c.branchId)}</td>
                <td>{t(`modes.${c.deliveryMode}`)}</td>
                <td className="num">{t("classes.seatsValue", { enrolled: c.enrolledCount, capacity: c.capacity })}</td>
                <td>{c.startsOn}</td>
                <td>{t(`classStatus.${c.status}`)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      <details className="panel">
        <summary>{t("classes.create")}</summary>
        <ActionForm api="academic/classes" submitLabel={t("save")}>
          <label>{t("fields.code")}<input name="code" required className="compact" /></label>
          <label>
            {t("classes.level")}
            <select name="courseLevelId" required>
              {levels.map((l) => <option key={l.id} value={l.id}>{levelName.get(l.id)}</option>)}
            </select>
          </label>
          <label>
            {t("classes.branch")}
            <select name="branchId" required>
              {branches.map((b) => <option key={b.id} value={b.id}>{localized(b.name, locale)}</option>)}
            </select>
          </label>
          <label>
            {t("classes.mode")}
            <select name="deliveryMode" required>
              {deliveryModes.map((m) => <option key={m} value={m}>{t(`modes.${m}`)}</option>)}
            </select>
          </label>
          <label>{t("classes.capacity")}<input name="capacity" type="number" min={1} defaultValue={12} required className="compact" /></label>
          <label>{t("classes.startsOn")}<input name="startsOn" type="date" required /></label>
        </ActionForm>
      </details>
    </main>
  );
}
