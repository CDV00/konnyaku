# Test giao diện (Playwright)

Bộ test các luồng người dùng chính trên Chromium thật, chạy với một hệ thống dựng từ đầu (PostgreSQL + Mailpit, Api, Worker, web build production, `infra/scripts/seed_review.py`). CI chạy trong job `e2e` của mỗi PR.

**Quy tắc:** PR nào đổi điều người dùng nhìn thấy (màn hình, luồng, quyền, email, việc Worker làm mà người dùng thấy kết quả) phải thêm hoặc sửa luồng ở đây trong cùng PR, cùng với tài liệu bị ảnh hưởng (`docs/huong-dan-su-dung.md`, `docs/chay-local.md`…).

Cách bật/tắt, cổng, biến môi trường: [docs/chay-local.md](../../docs/chay-local.md#test-giao-diện-tự-động-playwright).

## Bố cục

| Đường dẫn | Nội dung |
| --- | --- |
| `tests/<luồng>.spec.ts` | Một file một luồng nghiệp vụ, đặt tên theo việc người dùng làm (`refund.spec.ts`, `password-reset.spec.ts`). Mỗi `test()` độc lập, chạy song song với các test khác |
| `support/api.ts` | Tạo dữ liệu qua API (fixture): `Api.admin()`, `createStaff`, `createClass`, `createLearner`, `buyAndPay`, `waitForSeat`, `holdLesson`, `enableTwoFactor`… |
| `support/ui.ts` | Thao tác giao diện dùng chung: `signIn`, `fillLogin`, `signOut`, `openSheet`, `hydrated`, `statValue`, `fetchStatus` |
| `support/i18n.ts` | `t("admin.billing.refund")`: chữ lấy từ catalog `packages/i18n/messages`, không gõ cứng |
| `support/env.ts` | URL, tài khoản của seed, `uniqueTag()` |
| `support/mailpit.ts` | `latestEmailTo(address)`, `linkIn(email, "/login/reset")` |
| `support/totp.ts` | Mã TOTP (2 bước) từ secret |
| `run.mjs`, `compose.yaml` | Dựng stack, seed, chạy Playwright, dọn dẹp |

## Thêm một luồng

1. Tạo `tests/<luồng>.spec.ts`:

   ```ts
   import { expect, test } from "@playwright/test";
   import { Api, createClass, createLearner } from "../support/api";
   import { t } from "../support/i18n";
   import { openSheet, signIn } from "../support/ui";

   test("điều phối: chuyển học viên sang lớp khác", async ({ page }) => {
     const admin = await Api.admin();
     const cls = await createClass(admin);            // dữ liệu riêng của test này
     const learner = await createLearner(admin);
     await signIn(page, "staff", "coordinator@review.example.com", "Review-Pass-123", /\/ops/);
     await page.goto(`/ops/classes/${cls.id}`);
     const sheet = await openSheet(page, t("admin.classes.transfer"));
     // ...
     await expect(page.getByText(learner.name)).toBeVisible();
   });
   ```

2. Chạy riêng luồng đó (xem "Chạy một spec").
3. Chạy cả bộ ít nhất một lần trước khi mở PR: các test chạy song song, lỗi do dùng chung dữ liệu chỉ hiện khi chạy cùng nhau.

## Đăng nhập theo vai trò

| Cần | Làm |
| --- | --- |
| Vai trò có sẵn trong seed, chỉ đọc hoặc thay đổi không ảnh hưởng test khác | `signIn(page, "staff", seed.admin, seed.staffPassword, /\/ops/)`; tài khoản trong `support/env.ts` và bảng ở `docs/chay-local.md` (`admin@`, `manager@`, `coordinator@`, `consultant@`, `teacher.ja@`… `@review.example.com`) |
| Học viên có sẵn | `signIn(page, "learn", seed.learner(12), seed.learnerPassword)` (realm `learn`, đích `/learn`) |
| Người dùng mà test sẽ thay đổi (đổi mật khẩu, bật 2FA, khoá…) | Tạo mới: `createStaff(admin, ["Accountant"], "accountant")`, `createLearner(admin)` — không đụng tài khoản seed |
| Vai trò có 2FA | `const secret = await enableTwoFactor(staff)` rồi `fillLogin(...)` và nhập `totp(secret, totpStep() + 1)` (API nhận lệch một bước, mỗi bước dùng một lần) — xem `refund.spec.ts` |
| Hai người cùng lúc | Realm khác nhau dùng chung một `context` được (cookie `kk_learn` / `kk_staff`); cùng realm thì `browser.newContext()` |
| Super-admin | `platform.spec.ts`: host `adminConsoleUrl`, secret TOTP cố định của seed |
| Gọi API thẳng (kiểm quyền) | `await Api.staff(email, password)` rồi `.call("GET", "/api/...")`; qua relay của web với cookie của trang: `fetchStatus(page, "/ops/api/relay/...")` |

## Dữ liệu

- **Test tự tạo dữ liệu nó thay đổi**, qua API (`support/api.ts`), tên có `uniqueTag()`. Nhờ vậy test chạy song song và chạy lại được trên cùng database. Không sửa bản ghi của seed mà test khác đang đọc (ví dụ đừng hoàn tiền đơn có sẵn, đừng điểm danh lớp `JP-N5-ON1`).
- Thiếu hàm tạo dữ liệu thì thêm vào `support/api.ts`, gọi đúng endpoint mà `seed_review.py` hoặc giao diện gọi. Cần dữ liệu nền mới cho mọi test (chương trình, gói học…) thì thêm vào `seed_review.py`.
- Lớp: `createClass(admin, { mode: "Online" | "Offline", startedDaysAgo, capacity, schedule })` — giáo viên riêng, lớp online thêm một host vào pool, nên không trùng lịch với test khác. Buổi đã diễn ra trên provider Fake: `holdLesson(admin, cls, session, learners)`.
- Email (`*@example.com` theo quy tắc dự án): đọc bằng `latestEmailTo(address)`.

## Chờ cho đúng

- Không `page.waitForTimeout`. Chờ điều kiện: `await expect(locator).toBeVisible()`, `expect.poll(...)`, `expect(async () => {...}).toPass()`.
- Việc của Worker (xếp lớp, trừ buổi, email, phòng học) chạy bất đồng bộ: poll API (`waitForSeat`) hoặc tải lại trang trong `toPass` cho đến khi thấy kết quả.
- Trang render từ server: gõ hoặc bấm trước khi React hydrate thì bị mất. Lấy phần tử đầu tiên sẽ tương tác qua `await hydrated(locator)` (đã có sẵn trong `signIn`, `openSheet`).
- Chọn phần tử theo vai trò và nhãn (`getByRole`, `getByLabel`) với chữ từ `t(...)`; nhãn trùng nhau (ô có gợi ý) thì dùng `locator('input[name="..."]')`.

## Chạy một spec

```bash
# Một lệnh, từ thư mục gốc repo: dựng stack mới, seed, chỉ chạy luồng refund
node frontend/e2e/run.mjs --no-build -- --grep refund

# Viết test: giữ stack chạy, rồi chạy lặp lại ở terminal khác
node frontend/e2e/run.mjs --serve
cd frontend/e2e && npx playwright test tests/refund.spec.ts --headed   # hoặc --ui, --repeat-each 3
```

Lỗi: `npx playwright show-report` (trong `frontend/e2e`) mở trace, video, ảnh chụp; log server ở `frontend/e2e/logs`. Test vừa qua vừa lỗi là lỗi cần sửa, không tăng timeout hay thêm retry để che.
