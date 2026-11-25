import { defineConfig, devices } from "@playwright/test";
import { tenantUrl } from "./support/env";

const ci = !!process.env.CI;

/**
 * End-to-end tests of the main user flows on a real browser against a full local stack (see run.mjs and
 * docs/chay-local.md). Each spec creates the data it changes, so the specs run in parallel and can run again on the
 * same seeded database.
 */
export default defineConfig({
  testDir: "./tests",
  fullyParallel: true,
  forbidOnly: ci,
  // One retry in CI only, and it is reported as "flaky": a flaky test is a bug to fix, not to hide.
  retries: ci ? 1 : 0,
  workers: ci ? 3 : 4,
  timeout: 90_000,
  expect: { timeout: 15_000 },
  reporter: ci ? [["github"], ["list"], ["html", { open: "never" }]] : [["list"], ["html", { open: "never" }]],
  use: {
    baseURL: tenantUrl,
    locale: "vi-VN",
    timezoneId: "Asia/Ho_Chi_Minh",
    trace: "retain-on-failure",
    video: "retain-on-failure",
    screenshot: "only-on-failure",
    actionTimeout: 15_000,
    navigationTimeout: 30_000,
  },
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"] } }],
});
