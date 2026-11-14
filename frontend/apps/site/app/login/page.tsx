import { getTranslations } from "next-intl/server";
import { LoginTabs } from "./login-tabs";

export default async function LoginPage({ searchParams }: { searchParams: Promise<{ realm?: string }> }) {
  const { realm } = await searchParams;
  const t = await getTranslations("login");
  return (
    <main style={{ maxWidth: 420, margin: "48px auto", fontFamily: "system-ui" }}>
      <h1>{t("title")}</h1>
      <LoginTabs initialRealm={realm === "staff" ? "staff" : "learn"} />
    </main>
  );
}
