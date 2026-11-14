import { getLocale, getTranslations } from "next-intl/server";
import { requireMe } from "@konnyaku/session";
import { ActionForm } from "../action-form";
import { getJson, localized, type Branch } from "../lib";

export default async function BranchesPage() {
  const { token } = await requireMe("staff");
  const t = await getTranslations("admin");
  const locale = await getLocale();
  const branches = await getJson<Branch[]>("/api/organization/branches", token, []);
  return (
    <main>
      <h1>{t("branches.title")}</h1>
      {branches.length === 0 ? (
        <p className="muted">{t("branches.empty")}</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>{t("fields.code")}</th>
              <th>{t("fields.name")}</th>
              <th>{t("fields.timeZone")}</th>
              <th>{t("branches.virtual")}</th>
            </tr>
          </thead>
          <tbody>
            {branches.map((b) => (
              <tr key={b.id}>
                <td>{b.code}</td>
                <td>{localized(b.name, locale)}</td>
                <td>{b.timeZone}</td>
                <td>{b.isVirtual ? t("yes") : t("no")}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      <details className="panel">
        <summary>{t("branches.create")}</summary>
        <ActionForm api="organization/branches" submitLabel={t("save")}>
          <label>{t("fields.code")}<input name="code" required className="compact" /></label>
          <label>{t("fields.nameVi")}<input name="name.vi" required /></label>
          <label>{t("fields.nameEn")}<input name="name.en" /></label>
          <label>{t("fields.timeZone")}<input name="timeZone" required defaultValue="Asia/Ho_Chi_Minh" /></label>
          <label className="inline"><input name="isVirtual" type="checkbox" />{t("branches.virtual")}</label>
        </ActionForm>
      </details>
    </main>
  );
}
