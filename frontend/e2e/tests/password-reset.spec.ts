import { expect, test } from "@playwright/test";
import { Api, createStaff } from "../support/api";
import { tenantHost, uniqueTag } from "../support/env";
import { t } from "../support/i18n";
import { latestEmailTo, linkIn } from "../support/mailpit";
import { fillLogin, hydrated, signIn } from "../support/ui";

test("forgot password: request, email in Mailpit, set a new password, sign in with it", async ({ page }) => {
  const staff = await createStaff(await Api.admin(), ["Consultant"], "reset");
  const newPassword = `E2e-${uniqueTag()}-7`;

  await test.step("request a reset link from the staff login", async () => {
    await page.goto("/login?realm=staff");
    await page.getByRole("link", { name: t("login.forgotStaff") }).click();
    await page.waitForURL(/\/login\/forgot/);
    const send = await hydrated(page.getByRole("button", { name: t("login.forgotPage.submit") }));
    await page.getByLabel(t("login.forgotPage.email")).fill(staff.email);
    await send.click();
    await expect(page.getByText(t("login.forgotPage.sent"))).toBeVisible();
  });

  await test.step("open the link of the email (sent by the Worker) and set the new password", async () => {
    const email = await latestEmailTo(staff.email);
    const link = linkIn(email, "/login/reset");
    expect(new URL(link).hostname).toBe(tenantHost);
    await page.goto(link);
    const save = await hydrated(page.getByRole("button", { name: t("login.resetPage.submit") }));
    await page.locator('input[name="newPassword"]').fill(newPassword);
    await page.locator('input[name="confirm"]').fill(newPassword);
    await save.click();
    await expect(page.getByText(t("login.resetPage.done"))).toBeVisible();
  });

  await test.step("the old password no longer works, the new one does", async () => {
    const panel = await fillLogin(page, "staff", staff.email, staff.password);
    await expect(panel.getByText(t("login.errors.identity_invalid_credentials"))).toBeVisible();
    await signIn(page, "staff", staff.email, newPassword, /\/ops/);
  });

  await test.step("the link is single use", async () => {
    const link = linkIn(await latestEmailTo(staff.email), "/login/reset");
    await page.goto(link);
    const save = await hydrated(page.getByRole("button", { name: t("login.resetPage.submit") }));
    await page.locator('input[name="newPassword"]').fill(`${newPassword}x`);
    await page.locator('input[name="confirm"]').fill(`${newPassword}x`);
    await save.click();
    await expect(page.getByText(t("login.errors.identity_reset_link_invalid"))).toBeVisible();
  });
});
