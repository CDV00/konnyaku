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

## Chạy thử trên trình duyệt

1. Bật hệ thống theo `docs/chay-local.md` (Claude Code: dùng cấu hình trong `.claude/launch.json` — api 5080, worker 5090, site 4000, learn 4001, teach 4002, ops 4003).
2. Mở `http://demo.localhost:4000/login`. Tài khoản demo (do `infra/scripts/seed_demo.py` tạo): admin `admin@demo.example.com` / `Admin-Pass-123`, giáo viên `teacher@demo.example.com` / `Teacher-Pass-123`, học viên `hv000001` + mật khẩu tạm script in ra.
3. Thử đúng luồng vừa sửa, cả ca lỗi; đổi sáng/tối; thu về 375px.
4. Mô phỏng buổi học đã diễn ra (provider Fake): `POST /api/workspace/dev/sessions/{id}/simulate` với tài khoản TenantAdmin/Coordinator.
5. Đọc console và log server; không có lỗi mới.

## Báo cáo

Nói rõ đã chạy gì và kết quả; phần nào chưa kiểm được (ví dụ chưa mở trang học viên) thì ghi ra, không nói "đã xong" khi chưa chạy.
