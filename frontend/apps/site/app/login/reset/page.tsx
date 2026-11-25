import Link from "next/link";
import { getTranslations } from "next-intl/server";
import { FormMessage } from "@konnyaku/ui/kk/blocks";
import { LoginCard } from "../card";
import { ResetForm } from "../password-forms";

export default async function ResetPage({ searchParams }: { searchParams: Promise<{ token?: string }> }) {
  const { token } = await searchParams;
  const t = await getTranslations("login.resetPage");
  return (
    <LoginCard
      title={t("title")}
      subtitle={t("hint")}
      footer={<Link href="/login/forgot" className="text-primary underline-offset-4 hover:underline">{t("again")}</Link>}
    >
      {token ? <ResetForm token={token} /> : <FormMessage>{t("invalid")}</FormMessage>}
    </LoginCard>
  );
}
