import { getFormatter, getTranslations } from "next-intl/server";
import { ShieldCheck } from "lucide-react";
import { Input } from "@konnyaku/ui/components/ui/input";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@konnyaku/ui/components/ui/table";
import { EmptyState, PageHeader, Panel, StatusBadge } from "@konnyaku/ui/kk/blocks";
import { Field } from "@konnyaku/ui/kk/field";
import { consoleGet, requireActiveOperator, statusTone, type Operator } from "../../lib";
import { ConsoleButton, ConsoleForm, ConsoleSheet } from "../../platform-form";

export default async function OperatorsPage() {
  const { me, token } = await requireActiveOperator();
  const t = await getTranslations("platform");
  const format = await getFormatter();
  const operators = await consoleGet<Operator[]>("operators", token, []);

  return (
    <div className="space-y-6">
      <PageHeader
        title={t("operators.title")}
        description={t("operators.description")}
        actions={
          <ConsoleSheet triggerLabel={t("operators.invite")} title={t("operators.invite")} description={t("operators.inviteHint")}>
            <ConsoleForm api="operators" submitLabel={t("operators.invite")} showResult={["email", "temporaryPassword"]}>
              <Field label={t("operators.displayName")}><Input name="displayName" required /></Field>
              <Field label={t("operators.email")}><Input name="email" type="email" required /></Field>
            </ConsoleForm>
          </ConsoleSheet>
        }
      />
      <Panel bodyClassName="p-0">
        {operators.length === 0 ? (
          <div className="p-5"><EmptyState icon={ShieldCheck} title={t("operators.title")} /></div>
        ) : (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead className="pl-5">{t("operators.columns.name")}</TableHead>
                <TableHead>{t("operators.columns.status")}</TableHead>
                <TableHead className="hidden md:table-cell">{t("operators.columns.totp")}</TableHead>
                <TableHead className="hidden lg:table-cell">{t("operators.columns.lastSignIn")}</TableHead>
                <TableHead className="pr-5 text-right" />
              </TableRow>
            </TableHeader>
            <TableBody>
              {operators.map((operator) => {
                const self = operator.id === me.id;
                return (
                  <TableRow key={operator.id}>
                    <TableCell className="pl-5">
                      <p className="font-medium">
                        {operator.displayName}
                        {self && <StatusBadge tone="primary" className="ms-2">{t("operators.you")}</StatusBadge>}
                      </p>
                      <p className="text-xs text-muted-foreground">{operator.email}</p>
                    </TableCell>
                    <TableCell><StatusBadge tone={statusTone[operator.status]}>{t(`status.${operator.status}`)}</StatusBadge></TableCell>
                    <TableCell className="hidden md:table-cell">
                      <StatusBadge tone={operator.totpEnrolled ? "success" : "warning"}>
                        {t(operator.totpEnrolled ? "operators.totpOn" : "operators.totpPending")}
                      </StatusBadge>
                    </TableCell>
                    <TableCell className="hidden text-muted-foreground lg:table-cell">
                      {operator.lastSignInAt ? format.dateTime(new Date(operator.lastSignInAt), { dateStyle: "medium", timeStyle: "short" }) : t("never")}
                    </TableCell>
                    <TableCell className="pr-5 text-right">
                      {!self &&
                        (operator.status === "Active" ? (
                          <ConsoleButton api={`operators/${operator.id}/disable`} label={t("operators.disable")} variant="ghost" />
                        ) : (
                          <ConsoleButton api={`operators/${operator.id}/enable`} label={t("operators.enable")} />
                        ))}
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
