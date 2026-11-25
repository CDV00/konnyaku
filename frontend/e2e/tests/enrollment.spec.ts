import { expect, test } from "@playwright/test";
import { Api, createClass, seedPackage } from "../support/api";
import { seed, uniqueTag } from "../support/env";
import { t } from "../support/i18n";
import { hydrated, openSheet, signIn, statValue } from "../support/ui";

test("admin: new student with an account, order, full payment, Worker enrolls, learner signs in the first time", async ({ page, browser }) => {
  const admin = await Api.admin();
  const cls = await createClass(admin); // starts today, 20 daily lessons
  const pkg = await seedPackage(admin, "ZH-20"); // 20 paid + 2 bonus lessons
  const name = `E2E Ân ${uniqueTag()}`;
  let username = "";
  let temporaryPassword = "";

  await signIn(page, "staff", seed.admin, seed.staffPassword, /\/ops/);

  await test.step("create the student with a learner account", async () => {
    await page.goto("/ops/students");
    const sheet = await openSheet(page, t("admin.students.create"));
    await sheet.getByLabel(t("admin.students.fullName")).fill(name);
    await sheet.getByLabel(t("admin.fields.phone")).fill("0907654321");
    await sheet.getByLabel(t("admin.fields.email")).fill(`${name.split(" ").pop()}@example.com`);
    await expect(sheet.getByRole("checkbox", { name: t("admin.students.createAccount") })).toBeChecked();
    await sheet.getByRole("button", { name: t("admin.save") }).click();
    const result = sheet.locator("output");
    await expect(result).toBeVisible();
    username = (await result.locator("code").nth(0).textContent())!.trim();
    temporaryPassword = (await result.locator("code").nth(1).textContent())!.trim();
    expect(username).not.toBe("");
    expect(temporaryPassword).not.toBe("");
    await page.keyboard.press("Escape");
  });

  await test.step("order for the class", async () => {
    await page.goto("/ops/billing");
    const sheet = await openSheet(page, t("admin.billing.createOrder"));
    await sheet.getByPlaceholder(t("admin.billing.findStudent")).fill(name);
    await sheet.getByRole("option", { name }).click();
    await sheet.getByLabel(t("admin.billing.package")).selectOption(pkg.id);
    await sheet.getByLabel(t("admin.billing.class")).selectOption(cls.id);
    await sheet.getByRole("button", { name: t("admin.save") }).click();
    await expect(sheet).toBeHidden();
  });

  await test.step("record the full payment", async () => {
    await page.goto(`/ops/billing?q=${encodeURIComponent(name)}`);
    const row = page.getByRole("row").filter({ hasText: name });
    await expect(row).toContainText(t("admin.orderStatus.PendingPayment"));
    const sheet = await openSheet(page, t("admin.billing.recordPayment"), row);
    await expect(sheet.getByLabel(t("admin.billing.amount"))).toHaveValue(String(pkg.price));
    await sheet.getByRole("button", { name: t("admin.billing.recordPayment") }).click();
    await expect(sheet).toBeHidden();
    await expect(row).toContainText(t("admin.orderStatus.Paid"));
  });

  await test.step("the Worker enrolls the student (class page roster)", async () => {
    await expect(async () => {
      await page.goto(`/ops/classes/${cls.id}`);
      await expect(page.getByText(name).first()).toBeVisible({ timeout: 2_000 });
    }).toPass({ timeout: 30_000 });
  });

  await test.step("first sign-in: forced password change, then the class schedule and credits", async () => {
    const context = await browser.newContext();
    const learner = await context.newPage();
    await signIn(learner, "learn", username, temporaryPassword, /\/learn\/change-password/);
    const save = await hydrated(learner.getByRole("button", { name: t("password.submit") }));
    await learner.getByLabel(t("password.current")).fill(temporaryPassword);
    await learner.getByLabel(t("password.next")).fill(`E2e-${uniqueTag()}-9`);
    await save.click();
    await learner.waitForURL(/\/learn$/);
    await expect(learner.getByRole("heading", { name: t("session.greeting", { name }) })).toBeVisible();
    await expect(statValue(learner, t("credits.title"))).toHaveText(String(pkg.paidCredits + pkg.bonusCredits));
    await expect(statValue(learner, t("credits.bonusLabel"))).toHaveText(String(pkg.bonusCredits));
    await expect(learner.getByText(t("schedule.lesson", { number: 1 }), { exact: true }).first()).toBeVisible();
    await context.close();
  });
});
