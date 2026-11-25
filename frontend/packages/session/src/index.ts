import { cache } from "react";
import { cookies, headers } from "next/headers";
import { redirect } from "next/navigation";

/** Two sign-in realms with separate cookies, so one browser can hold a learner and a staff session at once. */
export type Realm = "learn" | "staff";

export const sessionCookie: Record<Realm, string> = { learn: "kk_learn", staff: "kk_staff" };

export const apiUrl = process.env.KONNYAKU_API_URL ?? "http://localhost:5080";

export type Me = {
  userId: string;
  displayName: string;
  email: string | null;
  preferredLocale: string;
  tenantId: string;
  roles: string[];
};

/** Tenant host of the current request (the browser-facing Host header, without the port). */
export async function tenantHost(): Promise<string> {
  const host = (await headers()).get("host") ?? "";
  return host.split(":")[0] ?? "";
}

/**
 * Redirect with a relative Location, resolved by the browser against the tenant host it is on
 * (route handlers may see a normalized dev origin such as localhost instead of demo.localhost).
 */
export function redirectTo(location: string): Response {
  return new Response(null, { status: 307, headers: { Location: location } });
}

/** Entry point of every class: the API checks this person may attend, then the browser goes to the meeting. */
export async function joinSession(realm: Realm, sessionId: string, home: string): Promise<Response> {
  const token = await sessionToken(realm);
  if (!token) return redirectTo(`/login?realm=${realm}`);
  const response = await apiFetch(`/api/workspace/sessions/${encodeURIComponent(sessionId)}/join`, { token });
  if (!response.ok) return redirectTo(`${home}?meeting=unavailable`);
  const { meetingUri } = (await response.json()) as { meetingUri: string };
  // A room of the tenant's own site (built-in classroom): tell it which sign-in to use for its tickets.
  return redirectTo(meetingUri.startsWith("/room/") ? `${meetingUri}?as=${realm}` : meetingUri);
}

export async function apiFetch(path: string, init: RequestInit & { token?: string } = {}): Promise<Response> {
  const { token, ...rest } = init;
  const requestHeaders = new Headers(rest.headers);
  requestHeaders.set("X-Tenant-Host", await tenantHost());
  // The visitor's device and IP (sign-in sessions, security log, rate limits), not the web server's.
  const incoming = await headers();
  const forwardedFor = incoming.get("x-forwarded-for");
  const userAgent = incoming.get("user-agent");
  if (forwardedFor && !requestHeaders.has("X-Forwarded-For")) requestHeaders.set("X-Forwarded-For", forwardedFor);
  if (userAgent && !requestHeaders.has("User-Agent")) requestHeaders.set("User-Agent", userAgent);
  if (token) requestHeaders.set("Authorization", `Bearer ${token}`);
  if (rest.body && !requestHeaders.has("Content-Type")) requestHeaders.set("Content-Type", "application/json");
  return fetch(`${apiUrl}${path}`, { ...rest, headers: requestHeaders, cache: "no-store" });
}

export async function sessionToken(realm: Realm): Promise<string | undefined> {
  return (await cookies()).get(sessionCookie[realm])?.value;
}

/** Current member for a realm, or a redirect to the login page of the tenant host (once per request: layout + page). */
export const requireMe = cache(async (realm: Realm): Promise<{ me: Me; token: string }> => {
  const token = await sessionToken(realm);
  if (token) {
    const response = await apiFetch("/api/identity/me", { token });
    if (response.ok) return { me: (await response.json()) as Me, token };
  }
  redirect(`/login?realm=${realm}`);
});

/** First and last day (YYYY-MM-DD) of the current month in a time zone. */
export function currentMonth(timeZone: string): { from: string; to: string } {
  const today = new Intl.DateTimeFormat("en-CA", { timeZone, year: "numeric", month: "2-digit", day: "2-digit" }).format(new Date());
  const [year, month] = today.split("-").map(Number) as [number, number];
  const last = new Date(Date.UTC(year, month, 0)).getUTCDate();
  const mm = String(month).padStart(2, "0");
  return { from: `${year}-${mm}-01`, to: `${year}-${mm}-${String(last).padStart(2, "0")}` };
}

export type TenantInfo = {
  id: string;
  slug: string;
  name: string;
  defaultLocale: string;
  enabledLocales: string[];
  timeZone: string;
};

/** Tenant of the current host (name, locales, time zone); once per request. */
export const tenantInfo = cache(async (): Promise<TenantInfo | null> => {
  const response = await apiFetch("/api/platform/tenant");
  return response.ok ? ((await response.json()) as TenantInfo) : null;
});
