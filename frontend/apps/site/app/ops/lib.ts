import { apiFetch } from "@konnyaku/session";

/** GET from the API; an empty list (or the fallback) when the caller may not read it. */
export async function getJson<T>(path: string, token: string, fallback: T): Promise<T> {
  const response = await apiFetch(path, { token });
  return response.ok ? ((await response.json()) as T) : fallback;
}

export type Localized = Record<string, string>;

/** Name in the UI locale, else Vietnamese, else whatever language the center entered. */
export function localized(name: Localized | null | undefined, locale: string): string {
  if (!name) return "";
  return name[locale] ?? name.vi ?? Object.values(name)[0] ?? "";
}

export type Branch = { id: string; code: string; name: Localized; timeZone: string; isVirtual: boolean };
export type StaffMember = { userId: string; displayName: string; email: string | null; roles: string[] };
export type Level = { id: string; code: string; name: Localized; targetLevelCode: string; ordinal: number; sessionCount: number; sessionMinutes: number };
export type Program = {
  id: string;
  code: string;
  name: Localized;
  languageCode: string;
  frameworkCode: string;
  isPublic: boolean;
  levels: Level[];
};
export type ClassRow = {
  id: string;
  code: string;
  courseLevelId: string;
  branchId: string;
  deliveryMode: string;
  capacity: number;
  enrolledCount: number;
  startsOn: string;
  timeZone: string;
  status: string;
  sessionMinutes: number;
  languageCode: string;
};
export type Student = { id: string; fullName: string; birthYear: number | null; homeBranchId: string | null; userId: string | null };
export type Package = { id: string; code: string; name: Localized; paidCredits: number; bonusCredits: number; price: number; currency: string };

export const staffRoles = ["TenantAdmin", "BranchManager", "Coordinator", "Consultant", "Accountant", "Teacher"] as const;

export type Tone = "primary" | "success" | "warning" | "highlight" | "destructive" | "muted";

/** Badge colors per status value, shared by every ops table. */
export const statusTone: Record<string, Tone> = {
  // leads
  New: "highlight",
  Contacted: "primary",
  Converted: "success",
  Lost: "muted",
  // classes
  Planned: "muted",
  Open: "primary",
  Running: "success",
  Completed: "muted",
  Cancelled: "destructive",
  // sessions
  Scheduled: "muted",
  Provisioned: "primary",
  InProgress: "success",
  // enrollments
  Pending: "warning",
  Active: "success",
  Frozen: "warning",
  Rejected: "destructive",
  Transferred: "muted",
  Dropped: "destructive",
  // orders
  PendingPayment: "warning",
  Paid: "success",
  RefundRequired: "destructive",
  // payroll
  Locked: "success",
};
