import { getFormatter, getTranslations } from "next-intl/server";
import { CalendarDays, Gift, PlayCircle, Ticket } from "lucide-react";
import { apiFetch, requireMe } from "@konnyaku/session";
import { SessionList, type SessionView } from "@konnyaku/session/schedule";
import { EmptyState, PageHeader, Panel, StatCard } from "@konnyaku/ui/kk/blocks";
import { Fade } from "@konnyaku/ui/components/animate-ui/primitives/effects/fade";
import { LearnOnboarding } from "./learn-onboarding";

type Account = { total: number; bonusCredits: number };
type Recording = { sessionId: string; sequenceNo: number; startsAt: string };

export default async function HomePage() {
  const { me, token } = await requireMe("learn");
  const [sessionsResponse, creditsResponse, replayResponse] = await Promise.all([
    apiFetch("/api/scheduling/my/sessions", { token }),
    apiFetch("/api/billing/my/credits", { token }),
    apiFetch("/api/learning/my/recordings", { token }),
  ]);
  const sessions = sessionsResponse.ok ? ((await sessionsResponse.json()) as SessionView[]) : [];
  const accounts = creditsResponse.ok ? ((await creditsResponse.json()) as Account[]) : [];
  const recordings = replayResponse.ok ? ((await replayResponse.json()) as Recording[]) : [];
  const total = accounts.reduce((sum, a) => sum + a.total, 0);
  const bonus = accounts.reduce((sum, a) => sum + a.bonusCredits, 0);

  const t = await getTranslations("learn");
  const schedule = await getTranslations("schedule");
  const credits = await getTranslations("credits");
  const replay = await getTranslations("replay");
  const session = await getTranslations("session");
  const format = await getFormatter();
  const next = sessions.find((s) => s.status !== "Cancelled");

  return (
    <div className="space-y-8">
      <PageHeader title={session("greeting", { name: me.displayName })} description={t("subtitle")} />
      <LearnOnboarding />

      {total < 0 && (
        <p role="alert" className="rounded-xl border border-destructive/30 bg-destructive/10 px-4 py-3 text-sm text-destructive">
          {credits("owed", { owed: -total })}
        </p>
      )}

      <Fade inView className="grid gap-4 sm:grid-cols-3">
        <StatCard
          label={credits("title")}
          count={Math.max(total, 0)}
          icon={Ticket}
          hint={accounts.length === 0 ? credits("none") : credits("unit")}
        />
        <StatCard label={credits("bonusLabel")} count={bonus} icon={Gift} tone="highlight" hint={credits("unit")} />
        <StatCard
          label={t("nextLesson")}
          value={next ? format.dateTime(new Date(next.startsAt), { weekday: "short", day: "2-digit", month: "2-digit", hour: "2-digit", minute: "2-digit" }) : "—"}
          icon={CalendarDays}
          tone="success"
          hint={next ? schedule("lesson", { number: next.sequenceNo }) : schedule("empty")}
        />
      </Fade>

      <div className="grid gap-6 lg:grid-cols-[1.6fr_1fr]">
        <SessionList base="/learn" canJoin sessions={sessions} title={schedule("learning")} />
        <Panel title={replay("title")} bodyClassName="p-0">
          {recordings.length === 0 ? (
            <div className="p-5">
              <EmptyState icon={PlayCircle} title={replay("empty")} description={replay("emptyHint")} />
            </div>
          ) : (
            <ul className="divide-y">
              {recordings.map((r) => (
                <li key={r.sessionId} className="flex items-center gap-3 px-5 py-3">
                  <span className="grid size-9 place-items-center rounded-lg bg-primary/10 text-primary">
                    <PlayCircle className="size-4.5" />
                  </span>
                  <span className="text-sm">
                    {replay("lesson", { number: r.sequenceNo, date: format.dateTime(new Date(r.startsAt), { dateStyle: "medium" }) })}
                  </span>
                </li>
              ))}
            </ul>
          )}
        </Panel>
      </div>
    </div>
  );
}
