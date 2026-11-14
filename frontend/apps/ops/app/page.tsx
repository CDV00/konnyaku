import Link from "next/link";
import { getTranslations } from "next-intl/server";
import { requireMe } from "@konnyaku/session";

export default async function HomePage() {
  const { me } = await requireMe("staff");
  const t = await getTranslations("ops");
  const session = await getTranslations("session");
  return (
    <main style={{ fontFamily: "system-ui", padding: 24 }}>
      <h1>{t("title")}</h1>
      <p>{session("greeting", { name: me.displayName })}</p>
      <p>{session("roles", { roles: me.roles.join(", ") })}</p>
      <p><Link href="/leads">{t("leads")}</Link></p>
      <form method="post" action="/api/session/staff/logout">
        <button type="submit">{session("logout")}</button>
      </form>
    </main>
  );
}
