import { expect, test } from "@playwright/test";
import { seed, tenantHost } from "../support/env";
import { t } from "../support/i18n";
import { signIn, signOut } from "../support/ui";

test("learner and staff signed in at once in one browser; signing out of one keeps the other and the tenant host", async ({ page, context }) => {
  // Learner 12 and the English teacher are not touched by any other spec.
  await signIn(page, "learn", seed.learner(12), seed.learnerPassword);
  await expect(page.getByRole("heading", { name: t("session.greeting", { name: "Quân (demo)" }) })).toBeVisible();

  const teacher = await context.newPage();
  await signIn(teacher, "staff", "teacher.en@review.example.com", seed.staffPassword, /\/teach/);
  await expect(teacher.getByRole("heading", { name: t("session.greeting", { name: "GV tiếng Anh" }) })).toBeVisible();

  const cookies = (await context.cookies()).map((c) => c.name);
  expect(cookies).toEqual(expect.arrayContaining(["kk_learn", "kk_staff"]));

  await test.step("sign out of the staff realm: back to the staff login on the same tenant host", async () => {
    await signOut(teacher);
    await teacher.waitForURL((url) => url.pathname === "/login");
    expect(new URL(teacher.url()).hostname).toBe(tenantHost);
    expect(new URL(teacher.url()).searchParams.get("realm")).toBe("staff");
    await expect(teacher.getByRole("tab", { name: t("login.staffTab") })).toHaveAttribute("aria-selected", "true");
    // The staff area now asks for a sign-in again.
    await teacher.goto("/teach");
    await teacher.waitForURL(/\/login\?realm=staff/);
  });

  await test.step("the learner session is untouched", async () => {
    await page.reload();
    await expect(page).toHaveURL(/\/learn$/);
    await expect(page.getByRole("heading", { name: t("session.greeting", { name: "Quân (demo)" }) })).toBeVisible();
    expect((await context.cookies()).map((c) => c.name)).not.toContain("kk_staff");
  });

  await test.step("sign out of the learner realm too: learner login on the tenant host", async () => {
    await signOut(page);
    await page.waitForURL((url) => url.pathname === "/login");
    expect(new URL(page.url()).hostname).toBe(tenantHost);
    expect(new URL(page.url()).searchParams.get("realm")).toBe("learn");
    await page.goto("/learn");
    await page.waitForURL(/\/login\?realm=learn/);
  });
});
