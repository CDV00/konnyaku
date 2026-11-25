import Link from "next/link";
import { getFormatter, getTranslations } from "next-intl/server";
import { MailCheck } from "lucide-react";
import { requireMe } from "@konnyaku/session";
import { cn } from "@konnyaku/ui/lib/utils";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@konnyaku/ui/components/ui/table";
import { EmptyState, PageHeader, Panel, StatusBadge } from "@konnyaku/ui/kk/blocks";
import { ActionButton } from "../action-form";
import { getJson, statusTone } from "../lib";

type Message = {
  id: string;
  createdAt: string;
  templateKey: string;
  channel: string;
  recipientAddress: string;
  recipientName: string | null;
  locale: string;
  status: "Pending" | "Sent" | "Failed";
  attempts: number;
  lastError: string | null;
  subject: string | null;
  sentAt: string | null;
  sensitive: boolean;
};

const filters = ["all", "Failed", "Pending", "Sent"] as const;
type Filter = (typeof filters)[number];

export default async function NotificationsPage({ searchParams }: { searchParams: Promise<{ status?: string }> }) {
  const { token } = await requireMe("staff");
  const t = await getTranslations("notifications");
  const format = await getFormatter();
  const requested = (await searchParams).status;
  const filter: Filter = filters.includes(requested as Filter) ? (requested as Filter) : "all";
  const query = filter === "all" ? "" : `?status=${filter}`;
  const messages = await getJson<Message[]>(`/api/notification/messages${query}`, token, []);
  return (
    <div className="space-y-6">
      <PageHeader title={t("title")} description={t("description")} />
      <nav className="flex flex-wrap gap-2" aria-label={t("filterLabel")}>
        {filters.map((f) => (
          <Link
            key={f}
            href={f === "all" ? "/ops/notifications" : `/ops/notifications?status=${f}`}
            className={cn(
              "rounded-full border px-3 py-1 text-sm transition-colors",
              f === filter ? "border-primary bg-primary text-primary-foreground" : "hover:border-primary/40 hover:text-primary",
            )}
          >
            {t(`filter.${f}`)}
          </Link>
        ))}
      </nav>
      <Panel bodyClassName="p-0">
        {messages.length === 0 ? (
          <div className="p-5"><EmptyState icon={MailCheck} title={t("empty")} /></div>
        ) : (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead className="hidden pl-5 sm:table-cell">{t("colTime")}</TableHead>
                <TableHead className="pl-5 sm:pl-2">{t("colRecipient")}</TableHead>
                <TableHead className="hidden lg:table-cell">{t("colMessage")}</TableHead>
                <TableHead>{t("colStatus")}</TableHead>
                <TableHead className="pr-5 text-right">{t("colActions")}</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {messages.map((m) => {
                const templateLabel = t.has(`templates.${m.templateKey.replaceAll(".", "_")}`)
                  ? t(`templates.${m.templateKey.replaceAll(".", "_")}`)
                  : m.templateKey;
                const time = format.dateTime(new Date(m.sentAt ?? m.createdAt), { dateStyle: "short", timeStyle: "short" });
                const canResend = m.status === "Failed" || (m.status === "Sent" && !m.sensitive);
                return (
                  <TableRow key={m.id}>
                    <TableCell className="hidden pl-5 align-top text-muted-foreground tabular-nums sm:table-cell">{time}</TableCell>
                    <TableCell className="pl-5 align-top sm:pl-2">
                      <p className="font-medium">{m.recipientName ?? m.recipientAddress}</p>
                      <p className="max-w-40 truncate text-xs text-muted-foreground sm:max-w-56">{m.recipientAddress}</p>
                      <p className="text-xs text-muted-foreground lg:hidden">{templateLabel}</p>
                      <p className="text-xs text-muted-foreground tabular-nums sm:hidden">{time}</p>
                    </TableCell>
                    <TableCell className="hidden align-top lg:table-cell">
                      <p>{templateLabel}</p>
                      {m.subject && <p className="max-w-80 truncate text-xs text-muted-foreground">{m.subject}</p>}
                      <p className="text-xs text-muted-foreground">{t("meta", { channel: m.channel, locale: m.locale.toUpperCase() })}</p>
                    </TableCell>
                    <TableCell className="align-top">
                      <StatusBadge tone={statusTone[m.status]}>{t(`status.${m.status}`)}</StatusBadge>
                      {m.attempts > 1 && <p className="mt-1 text-xs text-muted-foreground">{t("attempts", { count: m.attempts })}</p>}
                      {m.status !== "Sent" && m.lastError && (
                        <p className="mt-1 max-w-64 break-words text-xs text-destructive" title={m.lastError}>
                          {m.lastError.length > 120 ? `${m.lastError.slice(0, 120)}…` : m.lastError}
                        </p>
                      )}
                    </TableCell>
                    <TableCell className="pr-5 text-right align-top">
                      {canResend && (
                        <ActionButton
                          api={`notification/messages/${m.id}/resend`}
                          label={t("resend")}
                          variant={m.status === "Failed" ? "default" : "ghost"}
                        />
                      )}
                    </TableCell>
                  </TableRow>
                );
              })}
            </TableBody>
          </Table>
        )}
      </Panel>
    </div>
  );
}
