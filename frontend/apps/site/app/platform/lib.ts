import { cache } from "react";
import { cookies } from "next/headers";
import { redirect } from "next/navigation";
import { apiFetch } from "@konnyaku/session";

/** Platform realm: its own httpOnly cookies, never sent with tenant sessions (staff / learn). */
export const operatorCookie = "kk_platform";
/** Short-lived challenge between the password step and the TOTP step (never readable by scripts). */
export const challengeCookie = "kk_platform_mfa";

export type Operator = {
  id: string;
  email: string;
  displayName: string;
  status: "Active" | "Disabled";
  totpEnrolled: boolean;
  mustChangePassword: boolean;
  recoveryCodesLeft: number;
  createdAt: string;
  lastSignInAt: string | null;
};

export type TenantUsage = { students: number; activeClasses: number; staff: number };

export type TenantSummary = {
  id: string;
  slug: string;
  name: string;
  status: "Active" | "Suspended";
  defaultLocale: string;
  enabledLocales: string[];
  timeZone: string;
  createdAt: string;
  suspendedAt: string | null;
  suspendedReason: string | null;
  primaryHost: string;
  hosts: string[];
  usage: TenantUsage | null;
};

export type AuditEntry = {
  id: string;
  at: string;
  actor: string;
  action: string;
  tenantId: string | null;
  tenantName: string | null;
  details: Record<string, string | null>;
  ipAddress: string | null;
};

export async function operatorToken(): Promise<string | undefined> {
  return (await cookies()).get(operatorCookie)?.value;
}

/**
 * Signed-in operator, else a redirect to the console sign-in (once per request: layout + page).
 * With a temporary password only the account page is usable: every other page redirects there.
 */
export const requireOperator = cache(async (): Promise<{ me: Operator; token: string }> => {
  const token = await operatorToken();
  if (token) {
    const response = await apiFetch("/api/platform/console/me", { token });
    if (response.ok) return { me: (await response.json()) as Operator, token };
  }
  redirect("/platform/login");
});

export async function requireActiveOperator(): Promise<{ me: Operator; token: string }> {
  const session = await requireOperator();
  if (session.me.mustChangePassword) redirect("/platform/account");
  return session;
}

/** GET from the console API; the fallback when it fails. */
export async function consoleGet<T>(path: string, token: string, fallback: T): Promise<T> {
  const response = await apiFetch(`/api/platform/console/${path}`, { token });
  return response.ok ? ((await response.json()) as T) : fallback;
}

export const statusTone = { Active: "success", Suspended: "destructive", Disabled: "muted" } as const;
