import { getTranslations } from "next-intl/server";
import { ShieldCheck, TriangleAlert } from "lucide-react";
import { Input } from "@konnyaku/ui/components/ui/input";
import { PageHeader, Panel } from "@konnyaku/ui/kk/blocks";
import { Field } from "@konnyaku/ui/kk/field";
import { requireOperator } from "../../lib";
import { ConsoleForm } from "../../platform-form";

/** The only page usable with a temporary password (every other console page redirects here). */
export default async function AccountPage() {
  const { me } = await requireOperator();
  const t = await getTranslations("platform.account");

  return (
    <div className="max-w-2xl space-y-6">
      <PageHeader title={t("title")} description={t("description")} />
      {me.mustChangePassword && (
        <p className="flex items-start gap-2 rounded-lg border border-warning/40 bg-warning/15 px-4 py-3 text-sm">
          <TriangleAlert className="mt-0.5 size-4 shrink-0" />
          {t("mustChange")}
        </p>
      )}
      <Panel title={t("change")}>
        <ConsoleForm
          api="me/password"
          submitLabel={t("change")}
          doneMessage={t("changed")}
          doneHref={me.mustChangePassword ? "/platform" : undefined}
        >
          <Field label={t("currentPassword")}>
            <Input name="currentPassword" type="password" autoComplete="current-password" required />
          </Field>
          <Field label={t("newPassword")} hint={t("newPasswordHint")}>
            <Input name="newPassword" type="password" autoComplete="new-password" minLength={12} required />
          </Field>
        </ConsoleForm>
      </Panel>
      <Panel title={t("security")}>
        <p className="flex items-center gap-2 text-sm">
          <ShieldCheck className="size-4 text-success" />
          {t("totp")}
        </p>
        <p className="mt-2 text-sm text-muted-foreground">{t("recoveryLeft", { count: me.recoveryCodesLeft })}</p>
      </Panel>
    </div>
  );
}
