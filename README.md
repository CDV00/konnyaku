# Konnyaku

Education ERP & LMS cho trung tâm tiếng Anh — online qua Google Meet, mở rộng Hybrid/cơ sở vật lý.
Stack: .NET 10 (ASP.NET Core) · Next.js App Router · PostgreSQL.

## Tài liệu phân tích

| Chương | Nội dung |
|---|---|
| [1. Mở rộng nghiệp vụ](docs/01-product-expansion.md) | 8 tính năng đột phá, 12 pain point vận hành, rủi ro Host Account Pooling |
| [2. Kiến trúc](docs/02-architecture.md) | Modular Monolith, Google Workspace Engine (sequence diagram), async/rate limit |
| [3. Dữ liệu lõi](docs/03-core-database.md) | ERD, bảng Session/ResourceBooking/Ledger/Attendance/Timesheet, múi giờ, audit, concurrency |
| [4. Lộ trình](docs/04-roadmap.md) | V1.0 MVP → V2.0 Scale-up → V3.0 AI, mục tiêu + Definition of Done |
| [5. Scaffolding](docs/05-scaffolding.md) | Cấu trúc thư mục BE/FE, NuGet/npm packages, checklist ngày đầu |
| [6. Tiến hóa kiến trúc](docs/06-evolution-strategy.md) | 10 đường cắt để tách module thành service không viết lại nghiệp vụ, tiêu chí & playbook tách, kế hoạch theo roadmap |

## Quyết định chính

1. **Modular Monolith** — 2 process (`Api`, `Worker`) từ một solution, schema DB riêng cho mỗi module; mỗi module hành xử như service ngay từ đầu (Contracts + event qua Outbox) để tách sau chỉ cần đổi adapter.
2. **PostgreSQL** — exclusion constraint `tstzrange` chống chồng lịch GV/phòng/host ngay ở DB.
3. **Credit ledger append-only** là nguồn sự thật cho học phí, học bù, bảo lưu, hoàn tiền, doanh thu chưa thực hiện.
4. **Meet participant log → điểm danh + timesheet tự động**: một dữ liệu dùng cho cả học phí lẫn lương.
5. **Host pool = interval coloring**, số license = peak concurrency; có `IVirtualRoomProvider` để đổi provider nếu mô hình pool gặp vướng điều khoản.
6. **UTC + IANA timezone + NodaTime**: lịch lớp neo theo múi giờ lớp, availability GV theo múi giờ GV (xử lý DST).
