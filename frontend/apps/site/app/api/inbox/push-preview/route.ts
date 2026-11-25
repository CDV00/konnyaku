import { NextResponse, type NextRequest } from "next/server";
import { getFormatter, getTranslations } from "next-intl/server";
import { apiFetch, sessionToken, type Realm } from "@konnyaku/session";
import { describeInboxItem, type InboxItem, type InboxTranslator } from "@konnyaku/session/inbox-text";
import { pushCookie } from "../push";

/**
 * Called by the service worker (/sw.js) when a push arrives: pushes carry no content, so it asks here what to show.
 * The subscription endpoint tells which sign-in of this browser the push was for; the answer is the newest unread item
 * of that person, in their language. Nothing when signed out (the worker then shows a generic notice).
 */
export async function POST(request: NextRequest) {
  const { endpoint } = ((await request.json().catch(() => ({}))) ?? {}) as { endpoint?: string };
  const realms: Realm[] = ["learn", "staff"];
  const realm = realms.find((r) => endpoint && request.cookies.get(pushCookie[r])?.value === endpoint);
  const token = realm ? await sessionToken(realm) : undefined;
  const t = (await getTranslations("inbox")) as unknown as InboxTranslator;
  const fallback = { title: t("push.fallbackTitle"), body: "", link: null as string | null, id: null as string | null };
  if (!realm || !token) return NextResponse.json(fallback, { headers: { "Cache-Control": "no-store" } });

  const response = await apiFetch("/api/notification/inbox?unread=true&size=1", { token });
  const page = response.ok ? ((await response.json()) as { items: InboxItem[] }) : null;
  const item = page?.items[0];
  if (!item) return NextResponse.json(fallback, { headers: { "Cache-Control": "no-store" } });

  const format = await getFormatter();
  const text = describeInboxItem(item, t, (value, options) => format.dateTime(value, options as Parameters<typeof format.dateTime>[1]));
  return NextResponse.json({ id: item.id, title: text.title, body: text.body, link: item.link }, { headers: { "Cache-Control": "no-store" } });
}
