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

/** Tenant host of the current request: set by the site proxy for zone apps, otherwise the Host header. */
export async function tenantHost(): Promise<string> {
  const h = await headers();
  const host = h.get("x-tenant-host") ?? h.get("host") ?? "";
  return host.split(":")[0] ?? "";
}

export async function apiFetch(path: string, init: RequestInit & { token?: string } = {}): Promise<Response> {
  const { token, ...rest } = init;
  const requestHeaders = new Headers(rest.headers);
  requestHeaders.set("X-Tenant-Host", await tenantHost());
  if (token) requestHeaders.set("Authorization", `Bearer ${token}`);
  if (rest.body && !requestHeaders.has("Content-Type")) requestHeaders.set("Content-Type", "application/json");
  return fetch(`${apiUrl}${path}`, { ...rest, headers: requestHeaders, cache: "no-store" });
}

export async function sessionToken(realm: Realm): Promise<string | undefined> {
  return (await cookies()).get(sessionCookie[realm])?.value;
}

/** Current member for a realm, or a redirect to the login page of the tenant host. */
export async function requireMe(realm: Realm): Promise<{ me: Me; token: string }> {
  const token = await sessionToken(realm);
  if (token) {
    const response = await apiFetch("/api/identity/me", { token });
    if (response.ok) return { me: (await response.json()) as Me, token };
  }
  // Zones run under a basePath, so a relative redirect would land on /<zone>/login: use the site origin.
  const origin = (await headers()).get("x-site-origin") ?? "";
  redirect(`${origin}/login?realm=${realm}`);
}

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
