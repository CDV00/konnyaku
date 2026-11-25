/**
 * Where the stack under test runs. run.mjs sets these; the defaults match its default ports, so `npx playwright test`
 * also works against a stack started with `node run.mjs --serve`.
 */
export const webPort = process.env.E2E_WEB_PORT ?? "4950";
export const apiUrl = process.env.E2E_API_URL ?? "http://localhost:5190";
export const mailpitUrl = process.env.E2E_MAILPIT_URL ?? "http://localhost:8030";

/** Tenant of infra/scripts/seed_review.py. Chromium resolves every *.localhost name to this machine. */
export const tenantHost = "review.localhost";
export const tenantUrl = `http://${tenantHost}:${webPort}`;
export const adminConsoleUrl = `http://admin.localhost:${webPort}`;

/** Accounts created by seed_review.py (dev-only passwords, documented in docs/chay-local.md). */
export const seed = {
  staffPassword: "Review-Pass-123",
  learnerPassword: "Learner-Pass-123",
  admin: "admin@review.example.com",
  consultant: "consultant@review.example.com",
  teacherJa: "teacher.ja@review.example.com",
  learner: (n: number) => `hv-review${String(n).padStart(2, "0")}`,
  operator: {
    email: "superadmin@platform.example.com",
    password: "Platform-Pass-123",
    totpSecret: "KONNYAKUDEVPLATFORMTOTP234567ABC",
  },
} as const;

/** A short unique tag per test, so tests never collide with each other or with earlier runs on the same database. */
export function uniqueTag(): string {
  return `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`;
}
