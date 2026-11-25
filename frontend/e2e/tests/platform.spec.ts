import { expect, test } from "@playwright/test";
import { adminConsoleUrl, seed, uniqueTag, webPort } from "../support/env";
import { t } from "../support/i18n";
import { totp, totpStep } from "../support/totp";
import { hydrated, openSheet } from "../support/ui";

test("super-admin: signs in with an authenticator code on the admin host and creates a center", async ({ page, browser }) => {
  const slug = `e2e-${uniqueTag()}`;
  const adminEmail = `admin@${slug}.example.com`;

  await test.step("password, then TOTP code (fixed dev secret of seed_review.py)", async () => {
    await page.goto(`${adminConsoleUrl}/platform/login`);
    const next = await hydrated(page.getByRole("button", { name: t("platform.login.continue") }));
    await page.getByLabel(t("platform.login.email")).fill(seed.operator.email);
    await page.getByLabel(t("platform.login.password")).fill(seed.operator.password);
    await next.click();
    // A code is accepted once: a retry uses the next step's code (the API allows one step of drift).
    await page.getByLabel(t("platform.login.code")).fill(totp(seed.operator.totpSecret, totpStep() + test.info().retry));
    await page.getByRole("button", { name: t("platform.login.verify") }).click();
    await expect(page.getByRole("heading", { level: 1, name: t("platform.tenants.title"), exact: true })).toBeVisible();
  });

  let temporaryPassword = "";
  await test.step("create a center with its first admin", async () => {
    const sheet = await openSheet(page, t("platform.tenants.create"));
    await sheet.getByLabel(t("platform.tenants.name")).fill(`E2E Trung tâm ${slug}`);
    await sheet.getByLabel(t("platform.tenants.slug")).fill(slug);
    await sheet.getByLabel(t("platform.tenants.adminName")).fill("E2E Quản trị");
    await sheet.getByLabel(t("platform.tenants.adminEmail")).fill(adminEmail);
    await sheet.getByRole("button", { name: t("platform.tenants.create") }).click();
    const result = sheet.locator("output");
    await expect(result).toContainText(`${slug}.localhost`);
    temporaryPassword = (await result.locator("code").last().textContent())!.trim();
    expect(temporaryPassword).not.toBe("");
  });

  await test.step("the new center's admin signs in on its own host and must change the password", async () => {
    const context = await browser.newContext({ baseURL: `http://${slug}.localhost:${webPort}` });
    const center = await context.newPage();
    await center.goto("/login?realm=staff");
    const panel = center.getByRole("tabpanel", { name: t("login.staffTab") });
    await panel.getByLabel(t("login.email")).fill(adminEmail);
    await panel.getByLabel(t("login.password")).fill(temporaryPassword);
    await panel.getByRole("button", { name: t("login.submit") }).click();
    await center.waitForURL(/\/ops\/account\?required=1/);
    await context.close();
  });
});
