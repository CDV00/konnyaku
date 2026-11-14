import Link from "next/link";
import { getTranslations } from "next-intl/server";
import { requireMe } from "@konnyaku/session";

const sections = ["leads", "students", "billing", "classes", "programs", "staff", "branches", "payroll"] as const;

export default async function HomePage() {
  const { me } = await requireMe("staff");
  const t = await getTranslations("ops");
  const session = await getTranslations("session");
  return (
    <main>
      <h1>{t("title")}</h1>
      <p>{session("greeting", { name: me.displayName })}</p>
      <p className="muted">{session("roles", { roles: me.roles.join(", ") })}</p>
      <ul>
        {sections.map((section) => (
          <li key={section}>
            <Link href={`/${section}`}>{t(`nav.${section}`)}</Link>
            <span className="muted">{t(`hint.${section}`)}</span>
          </li>
        ))}
      </ul>
      <form method="post" action="/api/session/staff/logout">
        <button type="submit">{session("logout")}</button>
      </form>
    </main>
  );
}
