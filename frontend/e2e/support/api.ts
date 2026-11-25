import { expect } from "@playwright/test";
import { apiUrl, seed, tenantHost, uniqueTag } from "./env";
import { totp, totpStep } from "./totp";

/**
 * Test data through the API (the same calls seed_review.py makes), so each browser test sets up only what it needs,
 * with names of its own: tests run in parallel and again on the same database without stepping on each other.
 */
export class Api {
  constructor(private readonly token?: string, private readonly host = tenantHost) {}

  async call<T = unknown>(method: string, path: string, body?: unknown): Promise<{ status: number; body: T }> {
    const response = await fetch(`${apiUrl}${path}`, {
      method,
      headers: {
        "Content-Type": "application/json",
        "X-Tenant-Host": this.host,
        ...(this.token ? { Authorization: `Bearer ${this.token}` } : {}),
      },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    const text = await response.text();
    return { status: response.status, body: (text ? JSON.parse(text) : null) as T };
  }

  async must<T = unknown>(method: string, path: string, body?: unknown): Promise<T> {
    const { status, body: result } = await this.call<T>(method, path, body);
    if (status >= 400) throw new Error(`${method} ${path} -> ${status} ${JSON.stringify(result)}`);
    return result;
  }

  get = <T = unknown>(path: string) => this.must<T>("GET", path);
  post = <T = unknown>(path: string, body?: unknown) => this.must<T>("POST", path, body ?? {});

  static async staff(email: string, password: string = seed.staffPassword): Promise<Api> {
    const signIn = await new Api().post<{ accessToken: string | null }>("/api/identity/staff/login", { email, password });
    if (!signIn.accessToken) throw new Error(`${email} needs a second factor; sign in through the UI instead`);
    return new Api(signIn.accessToken);
  }

  static admin = () => Api.staff(seed.admin);
}

type Branch = { id: string; code: string };
type Program = { code: string; levels: { id: string; code: string }[] };
type Package = { id: string; code: string; price: number; paidCredits: number; bonusCredits: number };
export type Staff = { userId: string; email: string; password: string; name: string };
export type Learner = { userId: string; studentId: string; username: string; password: string; name: string };
export type Session = { id: string; sequenceNo: number; startsAt: string; endsAt: string };
export type ClassInfo = { id: string; code: string; teacher: Staff; sessions: Session[] };

/** A new staff member (no forced password change; the dev API does not enforce 2FA). */
export async function createStaff(admin: Api, roles: string[], label: string): Promise<Staff> {
  const tag = uniqueTag();
  const email = `e2e.${label}.${tag}@example.com`;
  const name = `E2E ${label} ${tag}`;
  const created = await admin.post<{ userId: string }>("/api/identity/staff", {
    email, displayName: name, initialPassword: seed.staffPassword, roles, branchIds: [],
  });
  return { userId: created.userId, email, password: seed.staffPassword, name };
}

/** Turns on two-step sign-in for a staff member; returns the secret. Uses up the current TOTP step. */
export async function enableTwoFactor(staff: Staff): Promise<string> {
  const api = await Api.staff(staff.email, staff.password);
  const { secret } = await api.post<{ secret: string }>("/api/identity/me/2fa/setup");
  await api.post("/api/identity/me/2fa/enable", { code: totp(secret, totpStep()) });
  return secret;
}

const branchCache = new Map<string, Branch>();

async function branch(admin: Api, code: string): Promise<Branch> {
  if (!branchCache.has(code)) {
    for (const b of await admin.get<Branch[]>("/api/organization/branches")) branchCache.set(b.code, b);
  }
  return branchCache.get(code) ?? Promise.reject(new Error(`branch ${code} missing: was seed_review.py run?`));
}

async function level(admin: Api, program: string, code: string) {
  const programs = await admin.get<Program[]>("/api/academic/programs");
  const found = programs.find((p) => p.code === program)?.levels.find((l) => l.code === code);
  if (!found) throw new Error(`level ${program}/${code} missing: was seed_review.py run?`);
  return found;
}

export async function seedPackage(admin: Api, code = "ZH-20"): Promise<Package> {
  const found = (await admin.get<Package[]>("/api/billing/packages")).find((p) => p.code === code);
  if (!found) throw new Error(`package ${code} missing: was seed_review.py run?`);
  return found;
}

/**
 * A class of its own, Chinese HSK 1 (20 lessons, daily at 07:00), with a teacher of its own so no booking clashes with
 * other tests. Offline: Quận 1 campus without a room. Online: the Online campus on the Fake meeting provider, with one
 * more host in the pool for it. `startedDaysAgo` > 0 gives lessons that already took place.
 */
export async function createClass(
  admin: Api,
  options: { capacity?: number; startedDaysAgo?: number; schedule?: boolean; mode?: "Offline" | "Online" } = {},
): Promise<ClassInfo> {
  const { capacity = 20, startedDaysAgo = 0, schedule = true, mode = "Offline" } = options;
  const tag = uniqueTag().toUpperCase();
  const teacher = await createStaff(admin, ["Teacher"], "teacher");
  if (mode === "Online") await admin.post("/api/workspace/hosts", { email: `e2e.host.${tag.toLowerCase()}@example.com` });
  const starts = new Date(Date.now() - startedDaysAgo * 86_400_000).toISOString().slice(0, 10);
  const cls = await admin.post<{ id: string; code: string }>("/api/academic/classes", {
    code: `E2E-${tag}`,
    courseLevelId: (await level(admin, "ZH-HSK", "HSK1-A")).id,
    branchId: (await branch(admin, mode === "Online" ? "ONLINE" : "HCM-Q1")).id,
    deliveryMode: mode,
    capacity,
    startsOn: starts,
  });
  const sessions = schedule
    ? await admin.post<Session[]>(`/api/scheduling/classes/${cls.id}/schedule`, {
        daysOfWeek: [1, 2, 3, 4, 5, 6, 7], startTime: "07:00", teacherId: teacher.userId, roomId: null,
      })
    : [];
  return { id: cls.id, code: cls.code, teacher, sessions };
}

/** A student record with a learner account whose password is already changed (like the seed's hv-review*). */
export async function createLearner(admin: Api, name = `E2E Học viên ${uniqueTag()}`): Promise<Learner> {
  const account = await admin.post<{ userId: string; username: string; temporaryPassword: string }>("/api/identity/learners", {
    displayName: name, role: "Student",
  });
  const signIn = await new Api().post<{ accessToken: string }>("/api/identity/learn/login", {
    username: account.username, password: account.temporaryPassword,
  });
  await new Api(signIn.accessToken).post("/api/identity/learn/change-password", {
    currentPassword: account.temporaryPassword, newPassword: seed.learnerPassword,
  });
  const student = await admin.post<{ id: string }>("/api/crm/students", {
    fullName: name, birthYear: 2005, email: `${account.username}@example.com`,
    homeBranchId: (await branch(admin, "HCM-Q1")).id, userId: account.userId,
  });
  return { userId: account.userId, studentId: student.id, username: account.username, password: seed.learnerPassword, name };
}

/** Order + full payment; resolves with the order id. The Worker enrolls the student afterwards (see waitForSeat). */
export async function buyAndPay(admin: Api, learner: Learner, cls: ClassInfo, pkg: Package): Promise<string> {
  const order = await admin.post<{ id: string }>("/api/billing/orders", { studentId: learner.studentId, packageId: pkg.id, classId: cls.id });
  await admin.post(`/api/billing/orders/${order.id}/payments`, { amount: pkg.price, method: "bank_transfer", reference: `E2E-${order.id.slice(0, 8)}` });
  return order.id;
}

/** Waits until the Worker has handled the payment event and the student has a seat in the class. */
export async function waitForSeat(admin: Api, cls: ClassInfo, learner: Learner): Promise<string> {
  let enrollmentId = "";
  await expect
    .poll(
      async () => {
        const roster = await admin.get<{ studentId: string; enrollmentId: string }[]>(`/api/academic/classes/${cls.id}/roster`);
        enrollmentId = roster.find((r) => r.studentId === learner.studentId)?.enrollmentId ?? "";
        return enrollmentId;
      },
      { message: `${learner.name} enrolled in ${cls.code} by the Worker`, timeout: 30_000 },
    )
    .not.toBe("");
  return enrollmentId;
}

/**
 * A lesson that took place on the Fake meeting provider (dev endpoint, like seed_review.py): the people joined for the
 * whole lesson. Waits for the room (created by the Worker) and then for the draft attendance the Worker derives.
 */
export async function holdLesson(admin: Api, cls: ClassInfo, session: Session, learners: Learner[]): Promise<void> {
  const participants = [
    { userId: cls.teacher.userId, displayName: cls.teacher.name, joinedMinute: 0, leftMinute: 90 },
    ...learners.map((l) => ({ userId: l.userId, displayName: l.name, joinedMinute: 0, leftMinute: 90 })),
  ];
  await expect
    .poll(
      async () => {
        const { status, body } = await admin.call<{ code?: string }>("POST", `/api/workspace/dev/sessions/${session.id}/simulate`, {
          participants, recording: false,
        });
        if (status < 400) return "held";
        if (body?.code === "workspace.meeting_not_ready") return "waiting for the room";
        throw new Error(`simulate -> ${status} ${JSON.stringify(body)}`);
      },
      { message: `room of lesson ${session.sequenceNo} of ${cls.code} (Worker)`, timeout: 30_000 },
    )
    .toBe("held");
  const teacher = await Api.staff(cls.teacher.email, cls.teacher.password);
  await expect
    .poll(async () => (await teacher.get<unknown[]>(`/api/learning/sessions/${session.id}/attendance`)).length, {
      message: `draft attendance of lesson ${session.sequenceNo} (Worker)`,
      timeout: 30_000,
    })
    .toBe(learners.length);
}
