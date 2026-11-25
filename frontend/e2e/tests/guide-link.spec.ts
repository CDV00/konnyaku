import { expect, test } from "@playwright/test";
import { seed } from "../support/env";
import { t } from "../support/i18n";
import { signIn } from "../support/ui";

/** The quick guide card of each area links to the matching page of the full user guide (docs/huong-dan). */
const cases = [
  { realm: "staff", user: seed.admin, password: seed.staffPassword, home: /\/ops/, page: "bat-dau-nhanh.md" },
  { realm: "staff", user: "teacher.en@review.example.com", password: seed.staffPassword, home: /\/teach/, page: "giao-vien.md" },
  { realm: "learn", user: seed.learner(12), password: seed.learnerPassword, home: /\/learn/, page: "hoc-vien.md" },
] as const;

for (const c of cases) {
  test(`quick guide links to the full guide: ${c.page}`, async ({ page }) => {
    await signIn(page, c.realm, c.user, c.password, c.home);
    const link = page.getByRole("link", { name: t("onboarding.guide") });
    await expect(link).toBeVisible();
    await expect(link).toHaveAttribute("href", new RegExp(`/docs/huong-dan/${c.page.replace(".", "\.")}$`));
    await expect(link).toHaveAttribute("target", "_blank");
  });
}
