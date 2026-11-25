import { cookies } from "next/headers";
import { apiFetch, type Realm } from "@konnyaku/session";

/**
 * The Web Push endpoint this browser subscribed with, per realm, in an httpOnly cookie: sign-out deletes exactly that
 * subscription (a push subscription is personal data), and the service worker finds which sign-in a push belongs to.
 */
export const pushCookie: Record<Realm, string> = { learn: "kk_push_learn", staff: "kk_push_staff" };

export const pushCookieOptions = { httpOnly: true, sameSite: "lax" as const, path: "/", maxAge: 60 * 60 * 24 * 365 };

/** Sign-out: the API forgets this browser's push subscription before the session is revoked. */
export async function forgetPushSubscription(realm: Realm, token: string): Promise<void> {
  const endpoint = (await cookies()).get(pushCookie[realm])?.value;
  if (!endpoint) return;
  await apiFetch(`/api/notification/push/subscriptions?endpoint=${encodeURIComponent(endpoint)}`, { method: "DELETE", token }).catch(() => null);
}
