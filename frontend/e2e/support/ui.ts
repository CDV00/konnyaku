import { expect, type Locator, type Page } from "@playwright/test";
import { t } from "./i18n";

export type Realm = "learn" | "staff";

/**
 * Waits until React has hydrated an element (React keeps its props on hydrated DOM nodes under `__reactProps$…`).
 * Pages are server-rendered: a click or a typed value that lands before hydration is lost (a click does nothing, a
 * form posts natively, a controlled input is reset), which is the classic source of flaky browser tests.
 */
export async function hydrated(locator: Locator): Promise<Locator> {
  await expect
    .poll(() => locator.evaluate((element) => Object.keys(element).some((key) => key.startsWith("__reactProps"))), {
      message: "the page to be hydrated",
    })
    .toBe(true);
  return locator;
}

/** The password step of the login page, in the tab of one realm. Returns the visible tab panel. */
export async function fillLogin(page: Page, realm: Realm, id: string, password: string) {
  await page.goto(`/login?realm=${realm}`);
  const panel = page.getByRole("tabpanel", { name: t(realm === "learn" ? "login.learnTab" : "login.staffTab") });
  const submit = await hydrated(panel.getByRole("button", { name: t("login.submit") }));
  await panel.getByLabel(t(realm === "learn" ? "login.username" : "login.email")).fill(id);
  await panel.getByLabel(t("login.password")).fill(password);
  await submit.click();
  return panel;
}

/** Signs in and waits for the landing page of the area (/learn, /teach or /ops). */
export async function signIn(page: Page, realm: Realm, id: string, password: string, landing: RegExp = realm === "learn" ? /\/learn/ : /\/(ops|teach)/) {
  await fillLogin(page, realm, id, password);
  await page.waitForURL(landing);
}

/** Signs out through the user menu of the top bar (a plain form post to /api/session/<realm>/logout). */
export async function signOut(page: Page) {
  await (await hydrated(page.getByRole("button", { name: t("shell.menu") }))).click();
  await page.getByRole("menuitem", { name: t("shell.logout") }).or(page.getByRole("button", { name: t("shell.logout") })).click();
}

/** Opens a drawer (FormSheet / ConsoleSheet) by its trigger and returns the dialog. */
export async function openSheet(page: Page, trigger: string, scope: Page | Locator = page) {
  await (await hydrated(scope.getByRole("button", { name: trigger, exact: true }).first())).click();
  const dialog = page.getByRole("dialog");
  await expect(dialog).toBeVisible();
  return dialog;
}

/** The big number of a StatCard on a dashboard (it counts up, so assertions on it poll). */
export const statValue = (page: Page, label: string) =>
  page.getByText(label, { exact: true }).first().locator("xpath=../following-sibling::p[1]");

/** Status of a same-origin request made by the page itself (with its cookies), as the app's own scripts would make it. */
export function fetchStatus(page: Page, path: string, method = "GET"): Promise<number> {
  return page.evaluate(
    async ([url, verb]) => (await fetch(url!, { method: verb, headers: { "Content-Type": "application/json" }, body: verb === "GET" ? undefined : "{}" })).status,
    [path, method],
  );
}
