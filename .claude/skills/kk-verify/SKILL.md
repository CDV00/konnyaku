---
name: kk-verify
description: Kiểm tra một thay đổi trong Konnyaku trước khi commit hoặc báo xong — build/test backend, lint/typecheck/build frontend, chạy hệ thống local và thử trên trình duyệt với tài khoản demo. Dùng sau khi sửa code, trước khi commit, hoặc khi được hỏi "chạy thử", "đã chạy được chưa".
---

# Kiểm tra thay đổi

Chạy đúng các bước CI (`.github/workflows/ci.yml`) cho phần đã đổi.

## Backend

```bash
cd backend && dotnet build Konnyaku.slnx && dotnet test --solution Konnyaku.slnx
```

- Cần Docker đang chạy (Testcontainers tự bật PostgreSQL cho integration test).
- `ArchitectureTests` fail vì snapshot contracts: chỉ cập nhật khi thay đổi là cố ý (`KONNYAKU_UPDATE_SNAPSHOTS=1`), xem skill `kk-integration-event`.
- Chạy riêng một nhóm test: `dotnet test --solution Konnyaku.slnx --filter "FullyQualifiedName~PayrollTests"`.

## Frontend

```bash
cd frontend && npx -y pnpm@10.34.9 lint && npx -y pnpm@10.34.9 typecheck && npx -y pnpm@10.34.9 build
```

Build lỗi `require() of ES Module` → ai đó đã nâng Tailwind khỏi 4.1.18.

## Test giao diện tự động (e2e)

Đổi giao diện, luồng đăng nhập, quyền, hoặc event giữa các module mà người dùng nhìn thấy (xếp lớp, trừ buổi, email): chạy bộ Playwright, giống job `e2e` của CI.

**Bắt buộc**: PR có thay đổi người dùng nhìn thấy phải thêm hoặc sửa luồng trong `frontend/e2e/tests` (hướng dẫn `frontend/e2e/README.md`: chỗ đặt spec/fixture, đăng nhập theo vai trò, tạo dữ liệu, chạy một spec) và cập nhật tài liệu bị ảnh hưởng. Chưa có luồng cho thay đổi thì chưa coi là kiểm tra xong.

```bash
npx -y pnpm@10.34.9 --dir frontend e2e                      # dựng stack riêng, seed, chạy hết (6–8 phút)
node frontend/e2e/run.mjs --no-build -- --grep attendance   # chạy lại một luồng, giữ bản build
```

- Cần Docker, .NET 10, Python 3; lần đầu cài trình duyệt: `npx -y pnpm@10.34.9 --dir frontend/e2e exec playwright install chromium`. Cổng riêng (5450, 5190, 4950, Mailpit 8030) nên chạy song song với hệ thống dev được.
- Lỗi: xem `frontend/e2e/playwright-report` (trace, video) và `frontend/e2e/logs/*.log`; trên CI là artifact `e2e-results`.
- Thêm luồng mới hoặc sửa nhãn/chữ mà test dùng: test lấy chữ từ catalog i18n, đổi key thì sửa test theo. Chi tiết cách viết test: `frontend/e2e/README.md`.
- Một test vừa qua vừa lỗi là lỗi phải sửa (test hoặc ứng dụng), không thêm `waitForTimeout` hay tăng retry.

## Chạy thử trên trình duyệt

1. Bật hệ thống theo `docs/chay-local.md` (Claude Code: dùng cấu hình trong `.claude/launch.json` — api 5080, worker 5090, site 4000).
2. Mở `http://demo.localhost:4000/login`. Tài khoản demo (do `infra/scripts/seed_demo.py` tạo): admin `admin@demo.example.com` / `Admin-Pass-123`, giáo viên `teacher@demo.example.com` / `Teacher-Pass-123`, học viên `hv000001` + mật khẩu tạm script in ra.
3. Thử đúng luồng vừa sửa, cả ca lỗi; đổi sáng/tối; thu về 375px.
4. Mô phỏng buổi học đã diễn ra (provider Fake): `POST /api/workspace/dev/sessions/{id}/simulate` với tài khoản có quyền xếp lịch (TenantAdmin, BranchManager, Coordinator). Chỉ bật ở Development (hoặc `Workspace__DevTools=true`).
5. Đăng nhập Quản trị/Kế toán ở dev không cần 2FA (`Auth:TwoFactor:Enforce=false` trong `appsettings.Development.json`); `accountant@review.example.com` của seed review đã bật 2FA (mã: `python infra/scripts/seed_review.py --totp <khoá in ra khi seed>`).
6. Đọc console và log server; không có lỗi mới.

## Báo cáo

Nói rõ đã chạy gì và kết quả; phần nào chưa kiểm được (ví dụ chưa mở trang học viên) thì ghi ra, không nói "đã xong" khi chưa chạy.
