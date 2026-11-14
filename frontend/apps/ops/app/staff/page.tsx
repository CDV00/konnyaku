import { getFormatter, getTranslations } from "next-intl/server";
import { requireMe } from "@konnyaku/session";
import { ActionForm } from "../action-form";
import { getJson, staffRoles, type StaffMember } from "../lib";

type Rate = { teacherId: string; hourlyRate: number; currency: string; effectiveFrom: string };

export default async function StaffPage() {
  const { token } = await requireMe("staff");
  const t = await getTranslations("admin");
  const format = await getFormatter();
  const [staff, rates] = await Promise.all([
    getJson<StaffMember[]>("/api/identity/staff", token, []),
    getJson<Rate[]>("/api/hr/rates", token, []),
  ]);
  // Rates come newest first per teacher: the first one seen is the latest.
  const latestRate = new Map<string, Rate>();
  for (const rate of rates) if (!latestRate.has(rate.teacherId)) latestRate.set(rate.teacherId, rate);

  return (
    <main>
      <h1>{t("staff.title")}</h1>
      {staff.length === 0 ? (
        <p className="muted">{t("staff.empty")}</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>{t("fields.displayName")}</th>
              <th>{t("fields.email")}</th>
              <th>{t("staff.roles")}</th>
              <th>{t("staff.rate")}</th>
            </tr>
          </thead>
          <tbody>
            {staff.map((member) => {
              const rate = latestRate.get(member.userId);
              return (
                <tr key={member.userId}>
                  <td>{member.displayName}</td>
                  <td>{member.email}</td>
                  <td>{member.roles.map((role) => t(`roles.${role}`)).join(", ")}</td>
                  <td>
                    {member.roles.includes("Teacher") && (
                      <>
                        {rate ? (
                          <span>
                            {t("staff.rateFrom", {
                              rate: format.number(rate.hourlyRate, { style: "currency", currency: rate.currency }),
                              from: rate.effectiveFrom,
                            })}
                          </span>
                        ) : (
                          <span className="muted">{t("staff.noRate")}</span>
                        )}
                        <ActionForm api={`hr/teachers/${member.userId}/rate`} method="PUT" submitLabel={t("staff.setRate")}>
                          <input name="hourlyRate" type="number" min={0} step={1000} required className="compact" aria-label={t("staff.hourlyRate")} />
                          <input name="currency" required defaultValue={rate?.currency ?? "VND"} size={3} aria-label={t("fields.currency")} />
                          <input name="effectiveFrom" type="date" required aria-label={t("staff.effectiveFrom")} />
                        </ActionForm>
                      </>
                    )}
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      )}
      <details className="panel">
        <summary>{t("staff.create")}</summary>
        <ActionForm api="identity/staff" submitLabel={t("save")}>
          <label>{t("fields.email")}<input name="email" type="email" required /></label>
          <label>{t("fields.displayName")}<input name="displayName" required /></label>
          <label>{t("staff.initialPassword")}<input name="initialPassword" type="password" minLength={8} autoComplete="new-password" /></label>
          <fieldset>
            <legend>{t("staff.roles")}</legend>
            {staffRoles.map((role) => (
              <label key={role} className="inline">
                <input type="checkbox" name="roles[]" value={role} defaultChecked={role === "Teacher"} />
                {t(`roles.${role}`)}
              </label>
            ))}
          </fieldset>
        </ActionForm>
      </details>
    </main>
  );
}
