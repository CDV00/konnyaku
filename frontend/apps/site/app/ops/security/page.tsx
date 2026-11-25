import { getFormatter, getTranslations } from "next-intl/server";
import { ShieldCheck, UserRound } from "lucide-react";
import { requireMe } from "@konnyaku/session";
import { Button } from "@konnyaku/ui/components/animate-ui/components/buttons/button";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@konnyaku/ui/components/ui/table";
import { EmptyState, PageHeader, Panel, StatusBadge } from "@konnyaku/ui/kk/blocks";
import { NativeSelect } from "@konnyaku/ui/kk/field";
import { ActionButton, ActionForm, FormSheet } from "../action-form";
import { getAccess, getJson, statusTone } from "../lib";

type LearnerAccount = {
  userId: string;
  displayName: string;
  username: string | null;
  roles: string[];
  status: string;
  mustChangePassword: boolean;
  lockedUntil: string | null;
  lastSeenAt: string | null;
};

type SecurityEvent = {
  id: string;
  occurredAt: string;
  kind: string;
  userId: string | null;
  subject: string | null;
  actorName: string | null;
  ipAddress: string | null;
  userAgent: string | null;
  detail: string | null;
};

const kinds = [
  "SignInSucceeded", "SignInFailed", "AccountLockedOut", "AccountSuspended", "AccountReactivated", "PasswordChanged", "PasswordResetByAdmin",
  "PasswordResetRequested", "PasswordResetCompleted", "TwoFactorEnabled", "TwoFactorDisabled", "TwoFactorResetByAdmin",
  "RecoveryCodesRegenerated", "RecoveryCodeUsed", "SignedOut", "SessionRevoked", "SignedOutEverywhere",
] as const;

const alertKinds = new Set(["SignInFailed", "AccountLockedOut", "AccountSuspended", "TwoFactorResetByAdmin", "PasswordResetByAdmin"]);

/** Account security of the center (administrator only): learner and parent accounts, and the security log. */
export default async function SecurityPage({ searchParams }: { searchParams: Promise<{ kind?: string }> }) {
  const { token } = await requireMe("staff");
  const t = await getTranslations("admin");
  const format = await getFormatter();
  const access = await getAccess(token);
  if (!access.can("identity.accounts")) {
    return <EmptyState icon={ShieldCheck} title={t("errors.forbidden")} />;
  }

  const { kind } = await searchParams;
  const filter = kinds.find((k) => k === kind);
  const [learners, events] = await Promise.all([
    getJson<LearnerAccount[]>("/api/identity/accounts/learners", token, []),
    getJson<SecurityEvent[]>(`/api/identity/security-events?take=200${filter ? `&kind=${filter}` : ""}`, token, []),
  ]);
  const when = (value: string) => format.dateTime(new Date(value), { dateStyle: "short", timeStyle: "short" });
  const detail = (value: string | null) => (value && t.has(`security.details.${value}`) ? t(`security.details.${value}`) : value);

  return (
    <div className="space-y-6">
      <PageHeader title={t("security.title")} description={t("security.description")} />

      <Panel title={t("security.accounts")} description={t("security.accountsHint")} bodyClassName="p-0">
        {learners.length === 0 ? (
          <div className="p-5"><EmptyState icon={UserRound} title={t("security.noAccounts")} /></div>
        ) : (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead className="pl-5">{t("fields.displayName")}</TableHead>
                <TableHead className="hidden sm:table-cell">{t("security.username")}</TableHead>
                <TableHead className="hidden md:table-cell">{t("security.lastSeen")}</TableHead>
                <TableHead className="pr-5 text-right" />
              </TableRow>
            </TableHeader>
            <TableBody>
              {learners.map((a) => {
                const active = a.status === "Active";
                return (
                  <TableRow key={a.userId} className={active ? "" : "opacity-70"}>
                    <TableCell className="pl-5">
                      <p className="flex flex-wrap items-center gap-2 font-medium">
                        {a.displayName}
                        {a.roles.map((role) => <StatusBadge key={role} tone="muted">{t(`roles.${role}`)}</StatusBadge>)}
                        {!active && <StatusBadge tone={statusTone[a.status]}>{t(`staffStatus.${a.status}`)}</StatusBadge>}
                        {a.lockedUntil && <StatusBadge tone="warning">{t("security.lockedOut")}</StatusBadge>}
                        {a.mustChangePassword && <StatusBadge tone="highlight">{t("security.temporaryPassword")}</StatusBadge>}
                      </p>
                      <p className="text-xs text-muted-foreground sm:hidden">{a.username}</p>
                    </TableCell>
                    <TableCell className="hidden font-mono text-sm sm:table-cell">{a.username}</TableCell>
                    <TableCell className="hidden text-sm text-muted-foreground md:table-cell">{a.lastSeenAt ? when(a.lastSeenAt) : "—"}</TableCell>
                    <TableCell className="pr-5 text-right">
                      <span className="inline-flex flex-wrap items-start justify-end gap-2">
                        <FormSheet
                          triggerLabel={t("security.resetPassword")}
                          title={t("security.resetPasswordFor", { name: a.displayName })}
                          description={t("security.resetPasswordHint")}
                          variant="outline"
                          size="sm"
                          icon={false}
                        >
                          <ActionForm api={`identity/accounts/${a.userId}/reset-password`} submitLabel={t("security.resetPassword")} showResult={["username", "temporaryPassword"]}>
                            <span />
                          </ActionForm>
                        </FormSheet>
                        {active ? (
                          <ActionButton api={`identity/accounts/${a.userId}/suspend`} label={t("security.lock")} variant="ghost" confirm={t("security.lockConfirm")} />
                        ) : (
                          <ActionButton api={`identity/accounts/${a.userId}/activate`} label={t("security.unlock")} variant="ghost" />
                        )}
                        {active && (
                          <ActionButton api={`identity/accounts/${a.userId}/sign-out`} label={t("security.signOut")} variant="ghost" confirm={t("security.signOutConfirm")} />
                        )}
                      </span>
                    </TableCell>
                  </TableRow>
                );
              })}
            </TableBody>
          </Table>
        )}
      </Panel>

      <Panel
        title={t("security.log")}
        description={t("security.logHint")}
        bodyClassName="p-0"
        action={
          <form method="get" className="flex items-center gap-2">
            <NativeSelect name="kind" defaultValue={filter ?? ""} aria-label={t("security.kind")} className="h-8 w-auto">
              <option value="">{t("security.allKinds")}</option>
              {kinds.map((k) => <option key={k} value={k}>{t(`security.kinds.${k}`)}</option>)}
            </NativeSelect>
            <Button type="submit" size="sm" variant="outline">{t("security.filter")}</Button>
          </form>
        }
      >
        {events.length === 0 ? (
          <div className="p-5"><EmptyState icon={ShieldCheck} title={t("security.noEvents")} /></div>
        ) : (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead className="pl-5">{t("security.when")}</TableHead>
                <TableHead>{t("security.kind")}</TableHead>
                <TableHead className="hidden md:table-cell">{t("security.account")}</TableHead>
                <TableHead className="hidden lg:table-cell">{t("security.by")}</TableHead>
                <TableHead className="hidden pr-5 lg:table-cell">{t("security.ip")}</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {events.map((e) => (
                <TableRow key={e.id}>
                  <TableCell className="pl-5 text-sm whitespace-nowrap tabular-nums">{when(e.occurredAt)}</TableCell>
                  <TableCell>
                    <span className="flex flex-wrap items-center gap-2">
                      <StatusBadge tone={alertKinds.has(e.kind) ? "warning" : "muted"}>
                        {t.has(`security.kinds.${e.kind}`) ? t(`security.kinds.${e.kind}`) : e.kind}
                      </StatusBadge>
                      {e.detail && <span className="text-xs text-muted-foreground">{detail(e.detail)}</span>}
                    </span>
                    <span className="block text-xs text-muted-foreground md:hidden">{e.subject}</span>
                  </TableCell>
                  <TableCell className="hidden text-sm md:table-cell">{e.subject ?? "—"}</TableCell>
                  <TableCell className="hidden text-sm lg:table-cell">{e.actorName ?? "—"}</TableCell>
                  <TableCell className="hidden pr-5 font-mono text-xs lg:table-cell" title={e.userAgent ?? undefined}>{e.ipAddress ?? "—"}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </Panel>
    </div>
  );
}
