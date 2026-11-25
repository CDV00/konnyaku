import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import { requireMe, sessionToken, type Realm } from "@konnyaku/session";
import { Classroom } from "./classroom";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("room");
  return { title: t("title") };
}

/**
 * Built-in classroom (WebRTC mesh). Reached through the join link (/learn/join/{session}, /teach/join/{session}),
 * which adds ?as=<realm>: the sign-in (learner or staff) the room uses to ask for its tickets.
 */
export default async function RoomPage({ params, searchParams }: {
  params: Promise<{ code: string }>;
  searchParams: Promise<{ as?: string }>;
}) {
  const { code } = await params;
  const { as } = await searchParams;
  const realm: Realm = as === "staff" || as === "learn" ? as : (await sessionToken("staff")) ? "staff" : "learn";
  const { me } = await requireMe(realm);
  return <Classroom code={code} realm={realm} home={realm === "staff" ? "/teach" : "/learn"} selfName={me.displayName} />;
}
