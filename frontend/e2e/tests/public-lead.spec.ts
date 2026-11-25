import { expect, test } from "@playwright/test";
import { seed, uniqueTag } from "../support/env";
import { t } from "../support/i18n";
import { hydrated, signIn } from "../support/ui";

test("visitor: public site in both languages, registration form, lead reaches the consultant", async ({ page, browser }) => {
  const name = `E2E Khách ${uniqueTag()}`;

  await test.step("public home page in English and Vietnamese", async () => {
    await page.goto("/en");
    await expect(page.getByRole("heading", { name: t("site.programs", {}, "en") })).toBeVisible();
    await expect(page.locator("html")).toHaveAttribute("lang", "en");
    await page.goto("/vi");
    await expect(page.getByRole("heading", { name: t("site.programs") })).toBeVisible();
    await expect(page.locator("html")).toHaveAttribute("lang", "vi");
  });

  await test.step("registration form (captcha off in the test stack)", async () => {
    await page.getByRole("link", { name: t("site.register") }).first().click();
    await expect(page.getByRole("heading", { name: t("register.title") })).toBeVisible();
    const submit = await hydrated(page.getByRole("button", { name: t("register.submit") }));
    await page.getByLabel(t("register.studentName")).fill(name);
    await page.getByLabel(t("register.language")).selectOption({ index: 1 });
    await page.getByLabel(t("register.phone")).fill("0901234567");
    await page.getByLabel(t("register.email")).fill(`${name.split(" ").pop()}@example.com`);
    await page.getByLabel(t("register.goal")).fill("E2E: học giao tiếp");
    await page.getByRole("checkbox").check();
    await submit.click();
    await expect(page.getByRole("status").filter({ hasText: t("register.sentTitle") })).toBeVisible();
  });

  await test.step("the consultant finds the new lead in /ops/leads", async () => {
    const context = await browser.newContext();
    const staff = await context.newPage();
    await signIn(staff, "staff", seed.consultant, seed.staffPassword, /\/ops/);
    await staff.goto(`/ops/leads?q=${encodeURIComponent(name)}`);
    await expect(staff.getByRole("cell", { name })).toBeVisible();
    // Contact details stay masked in the list (shown only on request, which is logged).
    await expect(staff.getByRole("row", { name: new RegExp(name) })).toContainText("09xx xxx 567");
    await context.close();
  });
});
