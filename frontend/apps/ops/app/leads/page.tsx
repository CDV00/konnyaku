import { getFormatter, getTranslations } from "next-intl/server";
import { apiFetch, requireMe } from "@konnyaku/session";
import { ActionButton } from "../action-form";

type Lead = {
  id: string;
  studentName: string;
  phone: string | null;
  email: string | null;
  languageCode: string;
  goal: string | null;
  status: string;
  followUpDueAt: string | null;
};

const nextStatuses = ["Contacted", "Converted", "Lost"] as const;

export default async function LeadsPage() {
  const { token } = await requireMe("staff");
  const t = await getTranslations("ops");
  const admin = await getTranslations("admin");
  const format = await getFormatter();
  const response = await apiFetch("/api/crm/leads", { token });
  const leads = response.ok ? ((await response.json()) as Lead[]) : [];
  return (
    <main>
      <h1>{t("openLeads")}</h1>
      {leads.length === 0 ? (
        <p>{t("noLeads")}</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>{t("colName")}</th>
              <th>{t("colLanguage")}</th>
              <th>{t("colContact")}</th>
              <th>{t("colGoal")}</th>
              <th>{t("colStatus")}</th>
              <th>{t("colDue")}</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {leads.map((lead) => (
              <tr key={lead.id}>
                <td>{lead.studentName}</td>
                <td>{lead.languageCode}</td>
                <td>{lead.phone ?? lead.email}</td>
                <td>{lead.goal}</td>
                <td>{admin(`leadStatus.${lead.status}`)}</td>
                <td>{lead.followUpDueAt ? format.dateTime(new Date(lead.followUpDueAt), { dateStyle: "short", timeStyle: "short" }) : null}</td>
                <td>
                  {nextStatuses
                    .filter((status) => status !== lead.status)
                    .map((status) => (
                      <ActionButton key={status} api={`crm/leads/${lead.id}/status`} body={{ status }} label={admin(`leadActions.${status}`)} />
                    ))}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </main>
  );
}
