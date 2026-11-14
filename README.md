# Konnyaku

Nền tảng SaaS Education ERP & LMS cho trung tâm ngoại ngữ — nhiều trung tâm, mỗi trung tâm nhiều cơ sở, dạy nhiều ngôn ngữ (Anh, Nhật, Trung, Hàn, Pháp, Đức…), giao diện đa ngôn ngữ do người dùng chọn. Học online qua Google Meet, mở rộng Hybrid/cơ sở vật lý.
Stack: .NET 10 (ASP.NET Core) · Next.js App Router · PostgreSQL.

## Tài liệu phân tích

| Chương | Nội dung |
|---|---|
| [1. Mở rộng nghiệp vụ](docs/01-product-expansion.md) | 8 tính năng đột phá, 17 pain point vận hành, rủi ro Host Account Pooling |
| [2. Kiến trúc](docs/02-architecture.md) | Modular Monolith, Meeting Engine (sequence diagram), async/rate limit, điểm cần xác minh trong spike |
| [3. Dữ liệu lõi](docs/03-core-database.md) | ERD, bảng Session/ResourceBooking/HostLease/Ledger/Attendance/Timesheet, múi giờ, audit, concurrency |
| [4. Lộ trình](docs/04-roadmap.md) | **Nguồn duy nhất** về phạm vi V1.0 MVP → V2.0 Scale-up/SaaS → V3.0 AI, mục tiêu + Definition of Done |
| [5. Scaffolding](docs/05-scaffolding.md) | Cấu trúc thư mục BE/FE (4 app), NuGet/npm packages, ADR & checklist ngày đầu |
| [6. Tiến hóa kiến trúc](docs/06-evolution-strategy.md) | **Nguồn duy nhất** về danh sách module & event; 10 đường cắt, tách theo tenant (Silo), tiêu chí & playbook tách service |
| [7. Đa trung tâm, đa cơ sở, đa ngôn ngữ](docs/07-multi-tenant-multi-language.md) | **Nguồn duy nhất** về quy tắc đa tenant; identity xuyên tenant, RLS, cấu hình kế thừa, khung năng lực, i18n, đăng nhập 2 tab & trang công khai |

## Thuật ngữ

| Từ | Nghĩa |
|---|---|
| **Tenant** / trung tâm | Một khách hàng của Konnyaku (một pháp nhân, đơn vị tài chính) |
| **Cơ sở** (branch) | Địa điểm học của một trung tâm, kể cả cơ sở ảo "Online" |
| **GV** / **HV** | Giáo viên / Học viên |
| Module **`Workspace`** | Meeting Engine của Konnyaku (host pool, phòng Meet, recording) — khác *Google Workspace* (sản phẩm của Google) |
| App **`site` / `ops` / `teach` / `learn`** | Trang công khai & đăng nhập / vận hành / giáo viên / học viên & phụ huynh |
| Realm **`learn` / `staff`** | Hai phiên đăng nhập tách biệt: học viên·phụ huynh và giáo viên·nhân viên |

## Quyết định chính

1. **Modular Monolith** — 2 process (`Api`, `Worker`) từ một solution, schema DB riêng cho mỗi module; không JOIN/FK/transaction chéo module; mỗi module hành xử như service ngay từ đầu (Contracts chỉ đọc + event qua Outbox) để tách sau chỉ cần đổi adapter.
2. **Multi-tenant từ commit đầu tiên** — `tenant_id` mọi bảng nghiệp vụ, PostgreSQL Row-Level Security + query filter (fail-closed), Pool mặc định và chuyển được tenant lớn sang DB riêng; tích hợp Google/Zalo/thanh toán là của từng tenant.
3. **Tenant → Nhóm cơ sở → Cơ sở**: học viên và GV thuộc trung tâm, lớp thuộc cơ sở (kể cả cơ sở ảo Online); phân quyền theo phạm vi; cấu hình kế thừa Platform → Tenant → Nhóm cơ sở → Cơ sở.
4. **Ngôn ngữ giảng dạy tách khỏi level**: khung năng lực là dữ liệu (CEFR, JLPT, HSK, TOPIK, IELTS…), không hard-code.
5. **Ba khái niệm ngôn ngữ độc lập**: locale giao diện (user chọn), ngôn ngữ giảng dạy (lớp), ngôn ngữ liên lạc (người nhận). V1 có `vi` + `en`, không chuỗi cứng, ICU MessageFormat.
6. **Xác thực tập trung + hai realm**: `auth.konnyaku.app` cho mọi trung tâm; tab Học viên·Phụ huynh (Google hoặc tài khoản trung tâm cấp) và tab Giáo viên·Nhân viên (Google, 2FA) — cookie và audience riêng nên không xung đột phiên, không leo quyền; trang công khai + form đăng ký đổ thẳng vào CRM.
7. **PostgreSQL** — exclusion constraint `tstzrange` chống chồng lịch GV/phòng (`scheduling`) và host (`workspace`) ngay ở DB.
8. **Credit ledger append-only** (module `Billing`) là nguồn sự thật cho học phí, học bù, bảo lưu, hoàn tiền, doanh thu thực hiện theo cơ sở.
9. **Meet participant log → điểm danh + timesheet tự động** qua event: một dữ liệu dùng cho cả học phí lẫn lương.
10. **Host pool = interval coloring**, số license = peak concurrency, pool thuộc từng tenant; có `IVirtualRoomProvider` để đổi provider. Các tính năng Meet API chưa chắc chắn được kiểm chứng bằng spike 1 tuần trước V1.
11. **UTC + IANA timezone + NodaTime**: lịch lớp neo theo múi giờ lớp/cơ sở, availability GV theo múi giờ GV (xử lý DST), lịch nghỉ lễ là dữ liệu.
