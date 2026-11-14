import { getTranslations } from "next-intl/server";
import { requireMe } from "@konnyaku/session";
import { ChangePasswordForm } from "./change-password-form";

export default async function ChangePasswordPage() {
  await requireMe("learn");
  const t = await getTranslations("password");
  return (
    <main style={{ maxWidth: 420, margin: "48px auto", fontFamily: "system-ui" }}>
      <h1>{t("title")}</h1>
      <p>{t("required")}</p>
      <ChangePasswordForm />
    </main>
  );
}
