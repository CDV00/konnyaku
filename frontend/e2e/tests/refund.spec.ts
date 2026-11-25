import { readFile } from "node:fs/promises";
import { expect, test } from "@playwright/test";
import { Api, buyAndPay, createClass, createLearner, createStaff, enableTwoFactor, seedPackage, waitForSeat } from "../support/api";
import { t } from "../support/i18n";
import { totp, totpStep } from "../support/totp";
import { fillLogin, openSheet } from "../support/ui";

test("accountant: two-step sign-in, refunds an order waiting for a refund, downloads the receipt PDF", async ({ page }) => {
  const admin = await Api.admin();
  // A full class: the second paid order cannot get a seat and waits for a refund (decided by the Worker).
  const cls = await createClass(admin, { capacity: 1, schedule: false });
  const pkg = await seedPackage(admin, "ZH-20");
  const first = await createLearner(admin);
  await buyAndPay(admin, first, cls, pkg);
  await waitForSeat(admin, cls, first);
  const second = await createLearner(admin);
  const orderId = await buyAndPay(admin, second, cls, pkg);
  await expect
    .poll(async () => (await admin.get<{ items: { id: string; status: string }[] }>(`/api/billing/orders?status=refund_required&q=${encodeURIComponent(second.name)}`)).items[0]?.id, {
      message: "the order waits for a refund (Worker: class full)",
      timeout: 60_000,
    })
    .toBe(orderId);

  const accountant = await createStaff(admin, ["Accountant"], "accountant");
  const secret = await enableTwoFactor(accountant); // used the code of the current 30 s step

  await test.step("sign in with password and authenticator code", async () => {
    const panel = await fillLogin(page, "staff", accountant.email, accountant.password);
    // The next step's code: the API accepts one step of clock drift, and never the same step twice.
    await panel.getByLabel(t("login.mfa.code")).fill(totp(secret, totpStep() + 1));
    await panel.getByRole("button", { name: t("login.mfa.submit") }).click();
    await page.waitForURL(/\/ops/);
  });

  const row = page.getByRole("row").filter({ hasText: second.name });

  await test.step("refund the order", async () => {
    await page.goto(`/ops/billing?status=refund_required&q=${encodeURIComponent(second.name)}`);
    await expect(row).toContainText(t("admin.orderStatus.RefundRequired"));
    const sheet = await openSheet(page, t("admin.billing.refund"), row);
    await expect(sheet.getByLabel(t("admin.billing.refundAmount"))).toHaveValue(String(pkg.price));
    await sheet.locator('textarea[name="reason"]').fill("E2E: lớp đã đủ chỗ");
    await sheet.getByRole("button", { name: t("admin.billing.refundSubmit") }).click();
    await expect(sheet).toBeHidden();
    await page.goto(`/ops/billing?q=${encodeURIComponent(second.name)}`);
    await expect(row).toContainText(t("admin.orderStatus.Refunded"));
  });

  await test.step("download the receipt (phiếu thu) of the payment as a PDF", async () => {
    const sheet = await openSheet(page, t("admin.billing.details"), row);
    // Two documents now: the receipt of the payment and the refund voucher (phiếu chi).
    await expect(sheet.getByRole("link", { name: t("admin.billing.viewReceipt") })).toHaveCount(2);
    await sheet.getByRole("link", { name: t("admin.billing.viewReceipt") }).first().click();
    await expect(page.getByRole("heading", { name: new RegExp(t("admin.receipts.receiptTitle", { code: "" }).trim()) })).toBeVisible();
    const [download] = await Promise.all([page.waitForEvent("download"), page.getByRole("link", { name: t("admin.receipts.download") }).click()]);
    expect(download.suggestedFilename()).toMatch(/\.pdf$/);
    const file = await readFile((await download.path())!);
    expect(file.subarray(0, 5).toString()).toBe("%PDF-");
    // Every print or download is logged on the receipt page.
    await page.reload();
    await expect(page.getByRole("heading", { name: t("admin.receipts.prints") })).toBeVisible();
    await expect(page.getByText(t("admin.receipts.noPrints"))).toBeHidden();
  });
});
