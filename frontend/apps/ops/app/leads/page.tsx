import { getFormatter, getTranslations } from "next-intl/server";
import { Inbox, Mail, Phone } from "lucide-react";
import { requireMe } from "@konnyaku/session";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@konnyaku/ui/components/ui/table";
import { EmptyState, PageHeader, Panel, StatusBadge } from "@konnyaku/ui/kk/blocks";
import { ActionButton } from "../action-form";
import { getJson, statusTone } from "../lib";

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
  const leads = await getJson<Lead[]>("/api/crm/leads", token, []);
  return (
    <div className="space-y-6">
      <PageHeader title={t("openLeads")} description={t("leadsDescription")} />
      <Panel bodyClassName="p-0">
        {leads.length === 0 ? (
          <div className="p-5"><EmptyState icon={Inbox} title={t("noLeads")} /></div>
        ) : (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead className="pl-5">{t("colName")}</TableHead>
                <TableHead>{t("colContact")}</TableHead>
                <TableHead>{t("colLanguage")}</TableHead>
                <TableHead>{t("colStatus")}</TableHead>
                <TableHead>{t("colDue")}</TableHead>
                <TableHead className="pr-5 text-right">{t("colActions")}</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {leads.map((lead) => (
                <TableRow key={lead.id}>
                  <TableCell className="pl-5">
                    <p className="font-medium">{lead.studentName}</p>
                    {lead.goal && <p className="max-w-56 truncate text-xs text-muted-foreground">{lead.goal}</p>}
                  </TableCell>
                  <TableCell>
                    {lead.phone && (
                      <a href={`tel:${lead.phone}`} className="flex items-center gap-1.5 hover:text-primary">
                        <Phone className="size-3.5" />
                        {lead.phone}
                      </a>
                    )}
                    {lead.email && (
                      <a href={`mailto:${lead.email}`} className="flex items-center gap-1.5 text-muted-foreground hover:text-primary">
                        <Mail className="size-3.5" />
                        {lead.email}
                      </a>
                    )}
                  </TableCell>
                  <TableCell><span className="rounded bg-muted px-1.5 py-0.5 text-xs font-semibold uppercase">{lead.languageCode}</span></TableCell>
                  <TableCell><StatusBadge tone={statusTone[lead.status]}>{admin(`leadStatus.${lead.status}`)}</StatusBadge></TableCell>
                  <TableCell className="text-muted-foreground tabular-nums">
                    {lead.followUpDueAt ? format.dateTime(new Date(lead.followUpDueAt), { dateStyle: "short", timeStyle: "short" }) : null}
                  </TableCell>
                  <TableCell className="pr-5">
                    <div className="flex justify-end gap-1.5">
                      {nextStatuses
                        .filter((status) => status !== lead.status)
                        .map((status) => (
                          <ActionButton
                            key={status}
                            api={`crm/leads/${lead.id}/status`}
                            body={{ status }}
                            label={admin(`leadActions.${status}`)}
                            variant={status === "Converted" ? "default" : status === "Lost" ? "ghost" : "outline"}
                          />
                        ))}
                    </div>
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
