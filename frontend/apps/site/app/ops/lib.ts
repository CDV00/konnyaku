import { apiFetch } from "@konnyaku/session";
import type { RoleGrant } from "./roles";

/** GET from the API; an empty list (or the fallback) when the caller may not read it. */
export async function getJson<T>(path: string, token: string, fallback: T): Promise<T> {
  const response = await apiFetch(path, { token });
  return response.ok && response.status !== 204 ? ((await response.json()) as T) : fallback;
}

export type Localized = Record<string, string>;

/** Days off shown in ops: from a month ago to a year ahead (YYYY-MM-DD). */
export function holidayWindow(): { from: string; to: string } {
  const day = 24 * 3600 * 1000;
  const iso = (d: Date) => d.toISOString().slice(0, 10);
  return { from: iso(new Date(Date.now() - 31 * day)), to: iso(new Date(Date.now() + 400 * day)) };
}

/** Name in the UI locale, else Vietnamese, else whatever language the center entered. */
export function localized(name: Localized | null | undefined, locale: string): string {
  if (!name) return "";
  return name[locale] ?? name.vi ?? Object.values(name)[0] ?? "";
}

export type Branch = { id: string; code: string; name: Localized; timeZone: string; isVirtual: boolean; isActive: boolean };
export type { RoleGrant };
export type StaffMember = { userId: string; displayName: string; email: string | null; roles: string[]; status: string; grants: RoleGrant[] };
export type Level = {
  id: string;
  code: string;
  name: Localized;
  targetLevelCode: string;
  ordinal: number;
  sessionCount: number;
  sessionMinutes: number;
  isActive: boolean;
};
export type Program = {
  id: string;
  code: string;
  name: Localized;
  languageCode: string;
  frameworkCode: string;
  isPublic: boolean;
  ageGroup: string | null;
  goal: string | null;
  levels: Level[];
  isActive: boolean;
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
export type Student = {
  id: string;
  fullName: string;
  birthYear: number | null;
  homeBranchId: string | null;
  userId: string | null;
  communicationLocale: string;
  isActive: boolean;
};
export type Package = {
  id: string;
  code: string;
  name: Localized;
  paidCredits: number;
  bonusCredits: number;
  price: number;
  currency: string;
  isActive: boolean;
};
export type Room = { id: string; branchId: string; code: string; capacity: number; isActive: boolean };
export type Holiday = { id: string; date: string; name: Localized; branchId: string | null };

/** Permissions as the API names them (see Konnyaku.Application.Authorization.Permissions). */
export type Permission =
  | "academic.programs"
  | "academic.classes"
  | "academic.classes.view"
  | "scheduling.schedules"
  | "organization.branches"
  | "organization.rooms"
  | "organization.holidays"
  | "identity.staff"
  | "hr.rates"
  | "billing.packages"
  | "billing.orders"
  | "billing.payments"
  | "crm.leads"
  | "crm.students"
  | "crm.students.view";

type PermissionScope = { permission: Permission; tenantWide: boolean; branchIds: string[] };

/**
 * What the signed-in member may change, to hide the actions they may not take. Only a convenience: the API checks
 * every write again (role and branch).
 */
export type Access = {
  /** For a record of `branchId` (null/undefined: a center-wide record). */
  can: (permission: Permission, branchId?: string | null) => boolean;
  /** In at least one branch. */
  any: (permission: Permission) => boolean;
};

export async function getAccess(token: string): Promise<Access> {
  const scopes = await getJson<PermissionScope[]>("/api/identity/me/access", token, []);
  const find = (permission: Permission) => scopes.find((s) => s.permission === permission);
  return {
    can: (permission, branchId) => {
      const scope = find(permission);
      return !!scope && (scope.tenantWide || (!!branchId && scope.branchIds.includes(branchId)));
    },
    any: (permission) => !!find(permission),
  };
}

export { staffRoles } from "./roles";

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
  // staff
  Suspended: "destructive",
};
