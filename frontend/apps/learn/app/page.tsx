import { getTranslations } from "next-intl/server";
import { apiFetch, requireMe } from "@konnyaku/session";
import { SessionList, type SessionView } from "@konnyaku/session/schedule";

export default async function HomePage() {
  const { me, token } = await requireMe("learn");
  const response = await apiFetch("/api/scheduling/my/sessions", { token });
  const sessions = response.ok ? ((await response.json()) as SessionView[]) : [];
  const schedule = await getTranslations("schedule");
  const t = await getTranslations("learn");
  const session = await getTranslations("session");
  return (
    <main style={{ fontFamily: "system-ui", padding: 24 }}>
      <h1>{t("title")}</h1>
      <p>{session("greeting", { name: me.displayName })}</p>
      <p>{session("roles", { roles: me.roles.join(", ") })}</p>
      <SessionList sessions={sessions} title={schedule("learning")} />
      <form method="post" action="/api/session/learn/logout">
        <button type="submit">{session("logout")}</button>
      </form>
    </main>
  );
}
