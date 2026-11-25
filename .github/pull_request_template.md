## Tóm tắt

<!-- Làm gì, vì sao; liên kết mục trong docs/09 nếu có. -->

## Kiểm tra trước khi merge

- [ ] Đã chạy kiểm tra theo skill `kk-verify` (backend build/test, frontend lint/typecheck/build).
- [ ] **Luồng e2e**: thay đổi người dùng nhìn thấy (màn hình, luồng, quyền, email, kết quả việc Worker làm) đã có luồng mới hoặc luồng sửa trong `frontend/e2e/tests` (hướng dẫn: `frontend/e2e/README.md`); job `e2e` xanh. Không có thay đổi nhìn thấy thì ghi lý do ở đây.
- [ ] **Tài liệu** bị ảnh hưởng đã cập nhật trong PR này: `docs/huong-dan-su-dung.md` (màn hình), `docs/chay-local.md` (cách chạy, cổng, biến), `README.md` (trạng thái), `docs/04`/`06`/`07` (phạm vi, module, event).
- [ ] Chuỗi giao diện có cả `vi` và `en`; lỗi API trả mã; dữ liệu test dùng `*@example.com`.
- [ ] Migration mới bật RLS; contracts đổi phá vỡ thì thêm `V2`.

## Lỗi tìm thấy / còn để sau

<!-- Lỗi phát hiện khi làm (đã sửa hay chưa), phần chưa làm. -->
