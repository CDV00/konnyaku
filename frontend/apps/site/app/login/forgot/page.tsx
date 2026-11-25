import Link from "next/link";
import { getTranslations } from "next-intl/server";
import { LoginCard } from "../card";
import { ForgotForm } from "../password-forms";

export default async function ForgotPage() {
  const t = await getTranslations("login.forgotPage");
  return (
    <LoginCard
      title={t("title")}
      subtitle={t("hint")}
      footer={<Link href="/login?realm=staff" className="text-primary underline-offset-4 hover:underline">{t("back")}</Link>}
    >
      <ForgotForm />
    </LoginCard>
  );
}
