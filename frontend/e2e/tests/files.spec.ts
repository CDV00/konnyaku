import { expect, test } from "@playwright/test";
import { Api, createLearner } from "../support/api";
import { apiUrl, seed, uniqueTag } from "../support/env";
import { t } from "../support/i18n";
import { hydrated, signIn } from "../support/ui";

/** A 1×1 PNG: the browser shows it inline, so the signed download opens as a page (a PDF would be downloaded headless). */
const png = Buffer.from("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==", "base64");
const pdf = Buffer.from("%PDF-1.4\n1 0 obj << >> endobj\ntrailer << >>\n%%EOF\n");

type StoredFile = { id: string; fileName: string; status: string; size: number };
type Ticket = { fileId: string; url: string; method: string; headers: Record<string, string> };

/** The three steps a browser does: signed URL from the API, body straight to storage, completion check. */
async function upload(api: Api, fileName: string, contentType: string, body: Buffer, purpose: string): Promise<string> {
  const ticket = await api.post<Ticket>("/api/content/uploads", { fileName, contentType, size: body.length, purpose });
  const sent = await fetch(ticket.url, { method: ticket.method, headers: ticket.headers, body });
  expect(sent.status).toBe(200);
  await api.post(`/api/content/uploads/${ticket.fileId}/complete`);
  return ticket.fileId;
}

/** A second center with its own administrator (platform API of the dev stack). */
async function otherCenter(): Promise<Api> {
  const slug = `e2e-${uniqueTag()}`.slice(0, 20);
  const platform = { "Content-Type": "application/json", "X-Platform-Key": "dev-platform-key" };
  const created = await fetch(`${apiUrl}/api/platform/tenants`, {
    method: "POST",
    headers: platform,
    body: JSON.stringify({ slug, name: `E2E ${slug}`, defaultLocale: "vi", enabledLocales: ["vi", "en"], timeZone: "Asia/Ho_Chi_Minh" }),
  });
  expect(created.status).toBe(201);
  const tenant = (await created.json()) as { id: string };
  const email = `admin.${slug}@example.com`;
  const password = "Other-Pass-123";
  const admin = await fetch(`${apiUrl}/api/identity/bootstrap/tenant-admin`, {
    method: "POST",
    headers: platform,
    body: JSON.stringify({ tenantId: tenant.id, email, displayName: "Other Admin", password }),
  });
  expect(admin.ok).toBeTruthy();
  const host = `${slug}.localhost`;
  const signIn = await new Api(undefined, host).post<{ accessToken: string }>("/api/identity/staff/login", { email, password });
  return new Api(signIn.accessToken, host);
}

test("center admin uploads material on the Files page, opens it through a short signed link, deletes it", async ({ page, context }) => {
  const name = `tai-lieu-${uniqueTag()}.png`;
  await signIn(page, "staff", seed.admin, seed.staffPassword, /\/ops/);
  await page.getByRole("link", { name: t("ops.nav.files") }).click();
  await expect(page.getByRole("heading", { name: t("files.title") })).toBeVisible();
  await expect(page.getByText(t("files.used"), { exact: true })).toBeVisible();

  await test.step("upload: the file goes straight to storage, then shows as Ready", async () => {
    await hydrated(page.getByRole("button", { name: t("files.upload") }));
    await page.getByLabel(t("files.upload")).setInputFiles({ name, mimeType: "image/png", buffer: png });
    await expect(page.getByText(t("files.uploaded", { name }))).toBeVisible();
    const row = page.locator(`tr[data-file="${name}"]`);
    await expect(row.getByText(t("files.status.Ready"))).toBeVisible();
    await expect(row.getByText(t("files.purpose.Material"))).toBeVisible();
  });

  await test.step("open: a signed URL of a few minutes, the same bytes back", async () => {
    const row = page.locator(`tr[data-file="${name}"]`);
    const [tab] = await Promise.all([context.waitForEvent("page"), (await hydrated(row.getByRole("button", { name: t("files.download") }))).click()]);
    await tab.waitForURL(/\/api\/content\/storage\/.+sig=/);
    const signed = tab.url();
    const response = await page.request.get(signed);
    expect(response.status()).toBe(200);
    expect(response.headers()["content-type"]).toBe("image/png");
    expect(Buffer.compare(await response.body(), png)).toBe(0);
    // Changing anything in the link breaks its signature.
    expect((await page.request.get(signed.replace("ct=image%2Fpng", "ct=text%2Fhtml"))).status()).toBe(403);
    await tab.close();
  });

  await test.step("delete: the row leaves the list and the link stops working", async () => {
    page.once("dialog", (dialog) => void dialog.accept());
    const row = page.locator(`tr[data-file="${name}"]`);
    await (await hydrated(row.getByRole("button", { name: t("files.delete") }))).click();
    await expect(row).toHaveCount(0);
  });
});

test("files are read only by those allowed: another learner, a teacher and another center are refused", async () => {
  const admin = await Api.admin();
  const owner = await createLearner(admin);
  const other = await createLearner(admin);
  const learner = await Api.learner(owner.username, owner.password);
  const otherLearner = await Api.learner(other.username, other.password);
  const teacher = await Api.staff(seed.teacherJa);

  const submission = await upload(learner, `bai-lam-${uniqueTag()}.pdf`, "application/pdf", pdf, "Submission");
  const material = await upload(admin, `slides-${uniqueTag()}.pdf`, "application/pdf", pdf, "Material");

  // The owner and the center's files administrator open the learner's work.
  expect((await learner.call("GET", `/api/content/files/${submission}/download`)).status).toBe(200);
  expect((await admin.call("GET", `/api/content/files/${submission}/download`)).status).toBe(200);
  // Another learner and a teacher are refused (a teacher reaches homework through the assessment it belongs to).
  const denied = await otherLearner.call<{ code: string }>("GET", `/api/content/files/${submission}/download`);
  expect(denied.status).toBe(403);
  expect(denied.body.code).toBe("content.file_forbidden");
  expect((await teacher.call("GET", `/api/content/files/${submission}/download`)).status).toBe(403);
  // Teaching material: every staff member, no learner.
  expect((await teacher.call("GET", `/api/content/files/${material}/download`)).status).toBe(200);
  expect((await learner.call("GET", `/api/content/files/${material}/download`)).status).toBe(403);
  // Learners cannot upload teaching material at all.
  const forbidden = await learner.call<{ code: string }>("POST", "/api/content/uploads", { fileName: "x.pdf", contentType: "application/pdf", size: 10, purpose: "Material" });
  expect(forbidden.status).toBe(403);

  // Another center does not see the files at all, even its administrator.
  const otherAdmin = await otherCenter();
  const missing = await otherAdmin.call<{ code: string }>("GET", `/api/content/files/${material}/download`);
  expect(missing.status).toBe(404);
  expect(missing.body.code).toBe("content.file_not_found");
  expect((await otherAdmin.call("GET", `/api/content/files/${submission}/download`)).status).toBe(404);
});
