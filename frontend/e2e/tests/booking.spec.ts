import { expect, test, type Browser, type Page } from "@playwright/test";
import { Api, createClass, createLearner, enroll, waitForBookings, type Learner } from "../support/api";
import { t } from "../support/i18n";
import { hydrated, signIn } from "../support/ui";

/** The row of a lesson on the learner's schedule or make-up options ("E2E-XYZ · Buổi 1"). */
const lessonRow = (page: Page, code: string, number: number) =>
  page.getByText(t("booking.classLesson", { code, number }), { exact: true }).locator("xpath=ancestor::li[1]");

async function learnerPage(browser: Browser, learner: Learner, path: string): Promise<Page> {
  const page = await (await browser.newContext()).newPage();
  await signIn(page, "learn", learner.username, learner.password);
  await page.goto(path);
  return page;
}

test("learner: asks leave in time, books the same lesson in another class; that class's teacher sees the make-up", async ({ page, browser }) => {
  const admin = await Api.admin();
  const home = await createClass(admin, { mode: "Online", startedDaysAgo: -2 });
  const other = await createClass(admin, { mode: "Online", startedDaysAgo: -3 });
  const learner = await createLearner(admin);
  await enroll(admin, home, learner);
  await waitForBookings(learner, home);

  await signIn(page, "learn", learner.username, learner.password);
  await page.goto("/learn/schedule");

  await test.step("leave for lesson 1, more than 12 hours ahead: no lesson spent, a make-up right instead", async () => {
    const row = lessonRow(page, home.code, 1);
    page.once("dialog", (dialog) => void dialog.accept());
    await (await hydrated(row.getByRole("button", { name: t("booking.postpone") }))).click();
    await expect(row.getByText(t("booking.status.Postponed"))).toBeVisible();
  });

  await test.step("the make-up options list lesson 1 of the other class; the learner books it", async () => {
    await page.getByRole("link", { name: t("booking.pick") }).first().click();
    await expect(page.getByRole("heading", { name: t("booking.options.title") })).toBeVisible();
    const option = lessonRow(page, other.code, 1);
    await (await hydrated(option.getByRole("button", { name: t("booking.options.book") }))).click();
    await expect(page.getByText(t("booking.options.notOpen"))).toBeVisible();
    await page.goto("/learn/schedule");
    await expect(lessonRow(page, other.code, 1).getByText(t("booking.kind.Makeup"))).toBeVisible();
  });

  await test.step("the teacher of the other class sees the make-up learner and the seat mode on the attendance page", async () => {
    const teacherPage = await (await browser.newContext()).newPage();
    await signIn(teacherPage, "staff", other.teacher.email, other.teacher.password, /\/teach/);
    await teacherPage.goto(`/teach/sessions/${other.sessions[0]!.id}/attendance?n=1`);
    const row = teacherPage.getByRole("listitem").filter({ hasText: learner.name });
    await expect(row.locator("p").getByText(t("attendance.kinds.Makeup"))).toBeVisible();
    await expect(row.getByText(t("attendance.modes.Online"))).toBeVisible();
    await expect(row.getByRole("radio", { name: t("attendance.statuses.Makeup") })).toBeVisible();
  });
});

test("learner: switches a hybrid lesson to on-site while the classroom has a seat", async ({ browser }) => {
  const admin = await Api.admin();
  const cls = await createClass(admin, { mode: "Hybrid", startedDaysAgo: -2, capacity: 5, roomCapacity: 1 });
  const first = await createLearner(admin);
  const second = await createLearner(admin);
  await enroll(admin, cls, first, "Online");
  await enroll(admin, cls, second, "Online");
  await waitForBookings(first, cls);
  await waitForBookings(second, cls);
  const toOffline = t("booking.switchTo", { mode: t("booking.mode.Offline") });

  const page = await learnerPage(browser, first, "/learn/schedule");
  const row = lessonRow(page, cls.code, 1);
  await (await hydrated(row.getByRole("button", { name: toOffline }))).click();
  await expect(row.getByRole("button", { name: t("booking.switchTo", { mode: t("booking.mode.Online") }) })).toBeVisible();
  await expect(row.getByText(t("booking.mode.Offline"), { exact: true })).toBeVisible();

  // The room holds one learner: the other learner can no longer move into the classroom for that lesson.
  const other = await learnerPage(browser, second, "/learn/schedule");
  await expect(lessonRow(other, cls.code, 1).getByText(t("booking.mode.Online"), { exact: true })).toBeVisible();
  await expect(lessonRow(other, cls.code, 1).getByRole("button", { name: toOffline })).toHaveCount(0);
});

test("two learners click the last classroom seat of a flexible class at once: only one gets it", async ({ browser }) => {
  const admin = await Api.admin();
  const cls = await createClass(admin, { mode: "Offline", startedDaysAgo: -2, capacity: 2, roomCapacity: 1, bookingMode: "Flexible" });
  const learners = [await createLearner(admin), await createLearner(admin)];
  for (const learner of learners) await enroll(admin, cls, learner);

  const pages = await Promise.all(learners.map((l) => learnerPage(browser, l, "/learn/flexible")));
  const book = await Promise.all(pages.map(async (page) => {
    await expect(page.getByRole("heading", { name: cls.code })).toBeVisible();
    const row = page.getByText(new RegExp(`^${t("booking.lesson", { number: 1 })} ·`)).first().locator("xpath=ancestor::li[1]");
    return hydrated(row.getByRole("button", { name: t("booking.flexible.book") }));
  }));
  await Promise.all(book.map((button) => button.click()));

  const booked = t("booking.flexible.booked", { mode: t("booking.mode.Offline") });
  const full = t("booking.errors.scheduling_session_full");
  await expect
    .poll(async () => {
      const outcomes = await Promise.all(pages.map(async (page) =>
        (await page.getByText(booked).count()) > 0 ? "booked" : (await page.getByText(full).count()) > 0 ? "full" : "pending"));
      return outcomes.sort().join(",");
    }, { message: "one learner gets the seat, the other is told the lesson is full" })
    .toBe("booked,full");
});
