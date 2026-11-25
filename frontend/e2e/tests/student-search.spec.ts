import { expect, test } from "@playwright/test";
import { Api, createStaff } from "../support/api";
import { seed, uniqueTag } from "../support/env";
import { t } from "../support/i18n";
import { fetchStatus, hydrated, signIn } from "../support/ui";

test("student list: search without accents finds accented names, contact details masked by role", async ({ page, browser }) => {
  const admin = await Api.admin();
  const tag = uniqueTag();
  const name = `Lê Ngọc Ân ${tag}`;
  await admin.post("/api/crm/students", { fullName: name, birthYear: 2008, phone: "0912 345 678", email: `an.${tag}@example.com` });
  const accountant = await createStaff(admin, ["Accountant"], "viewer");

  await test.step("admin: 'ngoc an' finds 'Lê Ngọc Ân'; contacts masked until revealed", async () => {
    await signIn(page, "staff", seed.admin, seed.staffPassword, /\/ops/);
    await page.goto("/ops/students");
    await (await hydrated(page.getByPlaceholder(t("admin.students.search")))).fill(`ngoc an ${tag}`);
    await page.waitForURL(/[?&]q=ngoc/);
    const row = page.getByRole("row").filter({ hasText: name });
    await expect(row).toBeVisible();
    await expect(page.getByRole("row")).toHaveCount(2); // header + the one match
    await expect(row).toContainText("09xx xxx 678");
    await expect(row).toContainText(`a***@example.com`);
    await row.getByRole("button", { name: t("admin.privacy.reveal") }).click();
    await expect(row).toContainText("0912345678");
    await expect(row).toContainText(`an.${tag}@example.com`);
    await row.getByRole("button", { name: t("admin.privacy.hide") }).click();
    await expect(row).toContainText("09xx xxx 678");
  });

  await test.step("a two-letter search without accents: 'an' lists 'Ân' (newest first)", async () => {
    await page.goto("/ops/students?q=an&sort=createdAt&dir=desc");
    await expect(page.getByRole("row").filter({ hasText: name })).toBeVisible();
  });

  await test.step("accountant (may view, not edit): masked contacts and no way to reveal them", async () => {
    const context = await browser.newContext();
    const viewer = await context.newPage();
    await signIn(viewer, "staff", accountant.email, accountant.password, /\/ops/);
    await viewer.goto(`/ops/students?q=${encodeURIComponent(`ngoc an ${tag}`)}`);
    const row = viewer.getByRole("row").filter({ hasText: name });
    await expect(row).toContainText("09xx xxx 678");
    await expect(row.getByRole("button", { name: t("admin.privacy.reveal") })).toHaveCount(0);
    await expect(viewer.getByRole("button", { name: t("admin.students.create") })).toHaveCount(0);
    // The API refuses the full details too (the button is not just hidden).
    expect(await fetchStatus(viewer, `/ops/api/relay/crm/students/${await studentId(admin, name)}/reveal`, "POST")).toBe(403);
    await context.close();
  });
});

async function studentId(admin: Api, name: string): Promise<string> {
  const page = await admin.get<{ items: { id: string }[] }>(`/api/crm/students?q=${encodeURIComponent(name)}`);
  return page.items[0]!.id;
}
