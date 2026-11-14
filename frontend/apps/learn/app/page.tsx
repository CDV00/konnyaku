import { getTranslations } from "next-intl/server";
import { apiFetch, requireMe } from "@konnyaku/session";
import { SessionList, type SessionView } from "@konnyaku/session/schedule";

export default async function HomePage() {
  const { me, token } = await requireMe("learn");
  const response = await apiFetch("/api/scheduling/my/sessions", { token });
  const sessions = response.ok ? ((await response.json()) as SessionView[]) : [];
  const schedule = await getTranslations("schedule");
  const creditsResponse = await apiFetch("/api/billing/my/credits", { token });
  const accounts = creditsResponse.ok ? ((await creditsResponse.json()) as { total: number; bonusCredits: number }[]) : [];
  const total = accounts.reduce((sum, a) => sum + a.total, 0);
  const bonus = accounts.reduce((sum, a) => sum + a.bonusCredits, 0);
  const credits = await getTranslations("credits");
  const t = await getTranslations("learn");
  const session = await getTranslations("session");
  return (
    <main style={{ fontFamily: "system-ui", padding: 24 }}>
      <h1>{t("title")}</h1>
      <p>{session("greeting", { name: me.displayName })}</p>
      <p>{session("roles", { roles: me.roles.join(", ") })}</p>
      <section>
        <h2>{credits("title")}</h2>
        {accounts.length === 0 ? (
          <p>{credits("none")}</p>
        ) : total < 0 ? (
          <p role="alert">{credits("owed", { owed: -total })}</p>
        ) : (
          <p>
            {credits("remaining", { total })} {bonus > 0 && credits("bonus", { bonus })}
          </p>
        )}
      </section>
      <SessionList canJoin sessions={sessions} title={schedule("learning")} />
      <form method="post" action="/api/session/learn/logout">
        <button type="submit">{session("logout")}</button>
      </form>
    </main>
  );
}
