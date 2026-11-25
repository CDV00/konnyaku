import { apiFetch } from "@konnyaku/session";
import type { RoleGrant } from "./roles";

/** GET from the API; an empty list (or the fallback) when the caller may not read it. */
export async function getJson<T>(path: string, token: string, fallback: T): Promise<T> {
  const response = await apiFetch(path, { token });
  return response.ok && response.status !== 204 ? ((await response.json()) as T) : fallback;
}

export type Localized = Record<string, string>;

/** One page of a list endpoint (the same shape in every module). */
export type Page<T> = { items: T[]; total: number; pageNumber: number; pageSize: number };

export const emptyPage = <T,>(): Page<T> => ({ items: [], total: 0, pageNumber: 1, pageSize: 25 });

/** Query string of a page: the URL's `searchParams` as Next.js hands them to the page. */
export type SearchParams = Promise<Record<string, string | string[] | undefined>>;

/**
 * The list parameters of the page URL forwarded to the API (only the known keys: the URL is shareable, the API call
 * stays exact). `defaults` apply when the URL does not set a key.
 */
export async function listQuery(searchParams: SearchParams, keys: readonly string[], defaults: Record<string, string> = {}) {
  const params = await searchParams;
  const query = new URLSearchParams();
  for (const key of ["page", "pageSize", "sort", "dir", ...keys]) {
    const raw = params[key];
    const value = Array.isArray(raw) ? raw[0] : raw;
    if (value) query.set(key, value);
    else if (defaults[key]) query.set(key, defaults[key]);
  }
  return query;
}

/** Name lookups of the ids on one page (students of a page of orders, a class roster): at most 200 ids per call. */
export async function namesOf(path: string, ids: (string | null | undefined)[], token: string, name: (row: { id: string; fullName: string }) => string = (r) => r.fullName) {
  const unique = [...new Set(ids.filter((id): id is string => !!id))].slice(0, 200);
  if (unique.length === 0) return new Map<string, string>();
  const page = await getJson<Page<{ id: string; fullName: string }>>(`${path}?ids=${unique.join(",")}&pageSize=200`, token, emptyPage());
  return new Map(page.items.map((row) => [row.id, name(row)]));
}

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
/** A row of the student list: birth year, guardian, phone and email arrive masked ("09xx xxx 567"). */
export type Student = {
  id: string;
  fullName: string;
  birthYear: string | null;
  guardianName: string | null;
  phone: string | null;
  email: string | null;
  homeBranchId: string | null;
  userId: string | null;
  communicationLocale: string;
  isActive: boolean;
  anonymized: boolean;
  createdAt: string;
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
  | "crm.students.view"
  | "crm.export"
  | "crm.privacy"
  | "notification.messages";

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
  // notifications
  Sent: "success",
  Failed: "destructive",
};
