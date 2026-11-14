import { getLocale, getTranslations } from "next-intl/server";
import { requireMe } from "@konnyaku/session";
import { ActionForm } from "../action-form";
import { getJson, localized, type Branch, type Student } from "../lib";

export default async function StudentsPage() {
  const { token } = await requireMe("staff");
  const t = await getTranslations("admin");
  const locale = await getLocale();
  const [students, branches] = await Promise.all([
    getJson<Student[]>("/api/crm/students", token, []),
    getJson<Branch[]>("/api/organization/branches", token, []),
  ]);
  const branchName = new Map(branches.map((b) => [b.id, localized(b.name, locale)]));

  return (
    <main>
      <h1>{t("students.title")}</h1>
      <details className="panel" open={students.length === 0}>
        <summary>{t("students.create")}</summary>
        <ActionForm endpoint="/ops/api/students" submitLabel={t("save")} showResult={["username", "temporaryPassword"]}>
          <label>{t("students.fullName")}<input name="fullName" required /></label>
          <label>{t("students.birthYear")}<input name="birthYear" type="number" min={1900} max={2100} className="compact" /></label>
          <label>{t("students.guardian")}<input name="guardianName" /></label>
          <label>{t("fields.phone")}<input name="phone" type="tel" /></label>
          <label>{t("fields.email")}<input name="email" type="email" /></label>
          <label>
            {t("classes.branch")}
            <select name="homeBranchId">
              <option value="">{t("students.noBranch")}</option>
              {branches.map((b) => <option key={b.id} value={b.id}>{localized(b.name, locale)}</option>)}
            </select>
          </label>
          <label className="inline"><input name="createAccount" type="checkbox" defaultChecked />{t("students.createAccount")}</label>
        </ActionForm>
      </details>
      {students.length === 0 ? (
        <p className="muted">{t("students.empty")}</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>{t("students.fullName")}</th>
              <th>{t("students.birthYear")}</th>
              <th>{t("classes.branch")}</th>
              <th>{t("students.account")}</th>
            </tr>
          </thead>
          <tbody>
            {students.map((s) => (
              <tr key={s.id}>
                <td>{s.fullName}</td>
                <td>{s.birthYear}</td>
                <td>{s.homeBranchId ? branchName.get(s.homeBranchId) : null}</td>
                <td>{s.userId ? t("yes") : <span className="muted">{t("no")}</span>}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </main>
  );
}
