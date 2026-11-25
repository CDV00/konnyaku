import { expect, test } from "@playwright/test";
import { Api, buyAndPay, createClass, createLearner, holdLesson, seedPackage, waitForSeat } from "../support/api";
import { apiUrl, tenantHost } from "../support/env";
import { t } from "../support/i18n";
import { fetchStatus, hydrated, signIn, statValue } from "../support/ui";

test("teacher: confirms the attendance of a lesson that took place, the learner's credits go down", async ({ page, browser }) => {
  const admin = await Api.admin();
  const cls = await createClass(admin, { mode: "Online", startedDaysAgo: 2 });
  const pkg = await seedPackage(admin, "ZH-20");
  const learner = await createLearner(admin);
  await buyAndPay(admin, learner, cls, pkg);
  await waitForSeat(admin, cls, learner);
  const lesson = cls.sessions.find((s) => new Date(s.endsAt) < new Date())!;
  await holdLesson(admin, cls, lesson, [learner]);
  const credits = pkg.paidCredits + pkg.bonusCredits;

  const learnerContext = await browser.newContext();
  const learnerPage = await learnerContext.newPage();
  await signIn(learnerPage, "learn", learner.username, learner.password);
  await expect(statValue(learnerPage, t("credits.title"))).toHaveText(String(credits));

  await test.step("the teacher opens the lesson from /teach and confirms the proposed attendance", async () => {
    await signIn(page, "staff", cls.teacher.email, cls.teacher.password, /\/teach/);
    await page.goto("/teach");
    await page.locator(`a[href^="/teach/sessions/${lesson.id}/attendance"]`).click();
    await expect(page.getByRole("heading", { name: t("attendance.title", { number: lesson.sequenceNo }) })).toBeVisible();
    const row = page.getByRole("listitem").filter({ hasText: learner.name });
    await expect(row.getByRole("radio", { name: t("attendance.statuses.Present") })).toHaveAttribute("aria-checked", "true");
    await (await hydrated(page.getByRole("button", { name: t("attendance.confirm") }))).click();
    await expect(page.getByRole("status").filter({ hasText: t("attendance.confirmed") })).toBeVisible();
  });

  await test.step("one lesson is deducted on the learner's page (Worker)", async () => {
    await expect(async () => {
      await learnerPage.reload();
      await expect(statValue(learnerPage, t("credits.title"))).toHaveText(String(credits - 1), { timeout: 3_000 });
    }).toPass({ timeout: 30_000 });
  });
  await learnerContext.close();

  await test.step("a teacher has no access to leads: not in the UI, not through the web relay, not on the API", async () => {
    await page.goto("/ops/leads");
    await page.waitForURL(/\/teach$/);
    expect(await fetchStatus(page, "/ops/api/relay/crm/leads")).toBe(403);
    const teacher = await Api.staff(cls.teacher.email, cls.teacher.password);
    const direct = await teacher.call("GET", "/api/crm/leads");
    expect(direct.status).toBe(403);
    // And the same request without a token is not let through either.
    const anonymous = await fetch(`${apiUrl}/api/crm/leads`, { headers: { "X-Tenant-Host": tenantHost } });
    expect(anonymous.status).toBe(401);
  });
});
