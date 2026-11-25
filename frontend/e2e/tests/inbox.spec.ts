import { expect, test, type Page } from "@playwright/test";
import { Api, createClass, createLearner, enroll, waitForBookings, type ClassInfo, type Learner } from "../support/api";
import { t } from "../support/i18n";
import { hydrated, signIn } from "../support/ui";

/** A learner of a fresh class (lessons every day from in 3 days), with the enrollment notice already read. */
async function learnerOfNewClass(): Promise<{ admin: Api; cls: ClassInfo; learner: Learner }> {
  const admin = await Api.admin();
  const cls = await createClass(admin, { startedDaysAgo: -3 });
  const learner = await createLearner(admin);
  await enroll(admin, cls, learner);
  await waitForBookings(learner, cls);
  const api = await Api.learner(learner.username, learner.password);
  // The enrollment notice comes from the Worker too: wait for it, then start from a clean inbox.
  await expect
    .poll(async () => (await api.get<{ unread: number }>("/api/notification/inbox/unread-count")).unread, { timeout: 60_000 })
    .toBeGreaterThan(0);
  await api.post("/api/notification/inbox/read-all");
  return { admin, cls, learner };
}

/** The bell of the page, once its realtime socket is up (the stack runs the realtime service and Redis). */
async function liveBell(page: Page) {
  const bell = page.getByTestId("inbox-bell");
  await expect(bell).toHaveAttribute("data-live", "true", { timeout: 30_000 });
  return bell;
}

test("learner: a session change appears in the bell live, without reloading; opening it marks it read", async ({ page }) => {
  const { admin, cls, learner } = await learnerOfNewClass();
  await signIn(page, "learn", learner.username, learner.password);
  const bell = await liveBell(page);
  await expect(page.getByTestId("inbox-unread")).toHaveCount(0);
  // Survives only if the page is never reloaded.
  await page.evaluate(() => ((window as unknown as { e2eNoReload: boolean }).e2eNoReload = true));

  const session = cls.sessions[1]!;
  const date = new Date(Date.now() + 10 * 86_400_000).toISOString().slice(0, 10);
  await admin.post(`/api/scheduling/sessions/${session.id}/reschedule`, { date, startTime: "18:00" });

  await test.step("the API commits, the Worker signals through Redis, the bell counts it", async () => {
    await expect(page.getByTestId("inbox-unread")).toHaveText("1", { timeout: 30_000 });
    expect(await page.evaluate(() => (window as unknown as { e2eNoReload?: boolean }).e2eNoReload)).toBe(true);
  });

  await test.step("the dropdown shows it in Vietnamese; opening it goes to the schedule and marks it read", async () => {
    await (await hydrated(bell)).click();
    const title = t("inbox.titles.session_rescheduled", { sequenceNo: session.sequenceNo, classCode: cls.code });
    const item = page.getByTestId("inbox-dropdown").getByTestId("inbox-item").filter({ hasText: title });
    await expect(item).toHaveAttribute("data-unread", "true");
    await item.click();
    await expect(page).toHaveURL(/\/learn\/schedule$/);
    await expect(page.getByTestId("inbox-bell")).toBeVisible();
    await expect(page.getByTestId("inbox-unread")).toHaveCount(0);
  });

  await test.step("the inbox page lists it as read", async () => {
    await page.goto("/learn/inbox");
    const item = page.getByTestId("inbox-page-list").getByTestId("inbox-item").first();
    await expect(item).toHaveAttribute("data-kind", "session.rescheduled");
    await expect(item).toHaveAttribute("data-unread", "false");
  });
});

test("learner: turns email off for cancellations; a cancellation still reaches the inbox, without email", async ({ page }) => {
  const { admin, cls, learner } = await learnerOfNewClass();
  await signIn(page, "learn", learner.username, learner.password);
  await page.goto("/learn/inbox/settings");

  await test.step("switch email off for cancelled sessions and save", async () => {
    const row = page.locator('[data-testid="pref-row"][data-kind="session.cancelled"]');
    const email = await hydrated(row.getByTestId("pref-email"));
    await expect(email).toHaveAttribute("data-state", "checked");
    await email.click();
    await expect(email).toHaveAttribute("data-state", "unchecked");
    await page.getByRole("button", { name: t("inbox.prefs.save") }).click();
    await expect(page.getByText(t("inbox.prefs.saved"))).toBeVisible();
    // Kinds without an email template say so instead of offering a switch.
    await expect(page.locator('[data-testid="pref-row"][data-kind="assessment.graded"]').getByText(t("inbox.prefs.noEmail"))).toBeVisible();
  });

  await page.goto("/learn/inbox");
  await liveBell(page);
  const session = cls.sessions[2]!;
  await admin.post(`/api/scheduling/sessions/${session.id}/cancel`, { reason: "E2E" });

  await test.step("the inbox page shows the cancellation live; mark all as read clears the bell", async () => {
    const title = t("inbox.titles.session_cancelled", { sequenceNo: session.sequenceNo, classCode: cls.code });
    const item = page.getByTestId("inbox-page-list").getByTestId("inbox-item").filter({ hasText: title });
    await expect(item).toHaveAttribute("data-unread", "true", { timeout: 30_000 });
    await expect(page.getByTestId("inbox-unread")).toHaveText("1");
    await page.getByRole("button", { name: t("inbox.markAllRead") }).click();
    await expect(page.getByTestId("inbox-unread")).toHaveCount(0);
    await expect(item).toHaveAttribute("data-unread", "false");
  });

  await test.step("the notification log has the teacher's email but none for the learner", async () => {
    // Inbox item and email are queued in the same transaction: once the item exists, a missing email is final.
    const log = await admin.get<{ templateKey: string; recipientAddress: string }[]>("/api/notification/messages?take=500");
    const cancelled = log.filter((m) => m.templateKey === "session.cancelled");
    expect(cancelled.some((m) => m.recipientAddress === cls.teacher.email)).toBe(true);
    expect(cancelled.some((m) => m.recipientAddress === `${learner.username}@example.com`)).toBe(false);
  });
});
