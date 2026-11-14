import { getFormatter, getTranslations } from "next-intl/server";

export type SessionView = {
  id: string;
  classId: string;
  sequenceNo: number;
  startsAt: string;
  endsAt: string;
  deliveryMode: "Online" | "Offline" | "Hybrid";
  status: string;
};

/** Upcoming sessions, formatted in the center's time zone (set in each app's i18n request config). */
export async function SessionList({ sessions, title }: { sessions: SessionView[]; title: string }) {
  const t = await getTranslations("schedule");
  const format = await getFormatter();
  return (
    <section>
      <h2>{title}</h2>
      {sessions.length === 0 ? (
        <p>{t("empty")}</p>
      ) : (
        <ul>
          {sessions.map((s) => (
            <li key={s.id}>
              {format.dateTimeRange(new Date(s.startsAt), new Date(s.endsAt), { weekday: "short", day: "2-digit", month: "2-digit", hour: "2-digit", minute: "2-digit" })}
              {" · "}
              {t("lesson", { number: s.sequenceNo })}
              {" · "}
              {t(`mode.${s.deliveryMode}`)}
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
