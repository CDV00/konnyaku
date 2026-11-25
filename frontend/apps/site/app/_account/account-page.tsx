import { getTranslations } from "next-intl/server";
import { apiFetch, requireMe } from "@konnyaku/session";
import { PageHeader } from "@konnyaku/ui/kk/blocks";
import { ChangePasswordPanel, SessionsPanel, TwoFactorPanel, type MySecurity } from "./account-panels";

const empty: MySecurity = { twoFactorEnabled: false, twoFactorRequired: false, recoveryCodesLeft: 0, mustChangePassword: false, sessions: [] };

/** A staff member's own account (ops and teach): password, two-step sign-in, signed-in devices. */
export async function StaffAccountPage({ home, required }: { home: string; required: boolean }) {
  const { token } = await requireMe("staff");
  const t = await getTranslations("account");
  const response = await apiFetch("/api/identity/me/security", { token });
  const security = response.ok ? ((await response.json()) as MySecurity) : empty;
  const mustChange = required || security.mustChangePassword;
  return (
    <div className="space-y-6">
      <PageHeader title={t("title")} description={t("description")} />
      <ChangePasswordPanel required={mustChange} home={home} />
      {!mustChange && <TwoFactorPanel security={security} />}
      {!mustChange && <SessionsPanel sessions={security.sessions} />}
    </div>
  );
}
