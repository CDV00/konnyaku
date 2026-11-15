---
name: kk-git-workflow
description: Quy ước nhánh, commit, cập nhật README/docs và merge của dự án Konnyaku. Dùng khi bắt đầu một tính năng/phase mới, khi commit, tạo PR hoặc merge vào main.
---

# Quy trình git

## Nhánh

- `main` luôn build được và qua CI. Không commit thẳng lên `main`.
- Mỗi phase hoặc tính năng một nhánh: `feature/phase-<n>-<ten-ngan>` hoặc `feature/<ten-ngan>` (ví dụ `feature/phase-8-ops-admin`, `feature/ui-animate-ui`).
- Bắt đầu từ `main` mới nhất: `git switch main && git pull && git switch -c feature/<ten>`.

## Commit

- Tiêu đề tiếng Anh, dạng Conventional Commits, chữ thường, không dấu chấm cuối: `feat: phase 7 hr and payroll`, `feat(ui): ...`, `fix(billing): ...`, `docs: ...`, `test: ...`, `chore: ...`.
- Một commit gói một thay đổi hoàn chỉnh (code + migration + test + i18n + docs liên quan).
- Không commit secret, `.env`, `appsettings.*.local.json`, `bin/`, `obj/`, `.next/`.
- Dữ liệu mẫu và test chỉ dùng `*@example.com`.

## Trước khi commit

1. Chạy kiểm tra theo skill `kk-verify` cho phần đã đổi.
2. Phase/tính năng lớn: cập nhật bảng **Trạng thái triển khai** trong `README.md` (cột "còn để sau" ghi rõ phần chưa làm).
3. Đổi phạm vi, module hoặc event: cập nhật `docs/04-roadmap.md`, `docs/06-evolution-strategy.md` (đây là nguồn duy nhất).
4. Đổi màn hình người dùng thấy: cập nhật `docs/huong-dan-su-dung.md`.
5. Đổi cách chạy local (cổng, lệnh, biến môi trường): cập nhật `docs/chay-local.md` và `.claude/launch.json`.

## Push và merge

- Chỉ push khi người phụ trách đồng ý; Claude Code không tự push hoặc merge nếu chưa được yêu cầu rõ ràng.
- Merge vào `main` sau khi CI xanh: `git switch main && git merge --no-ff feature/<ten>`, rồi push `main`.
- Không `push --force` lên `main`; không sửa migration đã nằm trên `main`.
