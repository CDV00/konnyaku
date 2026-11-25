# Konnyaku

Nền tảng SaaS Education ERP & LMS cho trung tâm ngoại ngữ — nhiều trung tâm, mỗi trung tâm nhiều cơ sở, dạy nhiều ngôn ngữ (Anh, Nhật, Trung, Hàn, Pháp, Đức…), giao diện đa ngôn ngữ do người dùng chọn. Học online qua Google Meet, mở rộng Hybrid/cơ sở vật lý.
Stack: .NET 10 (ASP.NET Core) · Next.js App Router · PostgreSQL.

## Chạy local

Hướng dẫn đầy đủ cách bật, tắt, bảng cổng và xử lý lỗi: [docs/chay-local.md](docs/chay-local.md). Frontend là một app Next.js chạy ở cổng 4000 (trang công khai, `/learn`, `/teach`, `/ops`). Người cùng phát triển đọc thêm [CLAUDE.md](CLAUDE.md) và các skill trong `.claude/skills/`.

Yêu cầu: .NET SDK 10, Node 20+, Docker, Python 3 (script seed). pnpm dùng qua `npx pnpm@10.34.9` nếu corepack trên máy lỗi.

```bash
docker compose -f infra/docker/docker-compose.yml up -d
```

```bash
cd backend && dotnet run --project src/Hosts/Konnyaku.Migrator
```

```bash
cd backend && dotnet run --project src/Hosts/Konnyaku.Api --launch-profile http
```

```bash
cd backend && dotnet run --project src/Hosts/Konnyaku.Worker
```

```bash
cd frontend && npx pnpm@10.34.9 install && npx pnpm@10.34.9 dev
```

```bash
python infra/scripts/seed_demo.py
```

- Mở `http://demo.localhost:4000/login` (trình duyệt tự trỏ `*.localhost` về máy mình; tenant xác định theo host).
  - Tab **Giáo viên · Nhân viên**: `admin@demo.example.com` / `Admin-Pass-123` → `/ops`; `teacher@demo.example.com` / `Teacher-Pass-123` → `/teach` (lịch dạy lớp JP-N5-01).
  - Tab **Học viên · Phụ huynh**: `hv000001` / mật khẩu tạm script in ra → bắt buộc đổi mật khẩu → `/learn`.
- Kỳ lương: `admin` mở `/ops/payroll` → tạo kỳ → chốt; giáo viên xem "Công tháng này" ở `/teach`.
- Giáo viên mở **Điểm danh** của buổi trong `/teach` → xác nhận → số buổi của học viên giảm ở `/learn`.
- Phòng học có sẵn (WebRTC): đổi provider của tenant sang `Konnyaku` (`PUT /api/workspace/connection` với `{"provider":"Konnyaku"}`), bật signaling (`docker compose -f infra/docker/docker-compose.yml up -d signaling`, cổng 5100) → buổi được xếp sau đó có **Vào lớp** mở `/room/{mã}`. Chi tiết: [docs/chay-local.md](docs/chay-local.md#phòng-học-có-sẵn-webrtc).
- Học viên bấm **Vào lớp** → `/learn/join/{buổi}` → phòng học mô phỏng (provider Fake). Mô phỏng kết thúc buổi học: `POST /api/workspace/dev/sessions/{id}/simulate` (vai trò TenantAdmin/Coordinator).
- Trang công khai: `http://demo.localhost:4000/vi` (danh mục khóa học), form đăng ký `http://demo.localhost:4000/vi/dang-ky`; lead hiện ở `/ops/leads`.
- API: `http://localhost:5080`, tài liệu API tại `/scalar`. Thao tác cấp nền tảng (tạo tenant) cần header `X-Platform-Key` (dev: `dev-platform-key`).
- Test backend: `cd backend && dotnet test --solution Konnyaku.slnx` (test tích hợp tự bật PostgreSQL bằng Testcontainers, cần Docker).
- Thêm module mới: thêm dòng role/schema trong `infra/docker/postgres/init/01-modules.sql` rồi tạo lại volume dev (`docker compose ... down -v`).
- Giao diện: package `frontend/packages/ui` (`@konnyaku/ui`) — Tailwind v4 (giữ 4.1.x vì Node 20.18 chưa hỗ trợ `require(esm)`), token màu sáng/tối trong `src/styles/globals.css`, component [animate-ui](https://animate-ui.com) + shadcn/ui chép vào `src/components` (code vendored, sửa tối thiểu), component riêng của Konnyaku trong `src/kk` (khung trang, thẻ thống kê, field, logo). Thêm component: `cd frontend/packages/ui && npx shadcn@latest add @animate-ui/<tên>` (registry khai báo trong `components.json`); sau đó kiểm tra import `cn` trỏ về `@konnyaku/ui/lib/utils`.

Hướng dẫn cho người dùng cuối (học viên, giáo viên, nhân viên vận hành): [docs/huong-dan-su-dung.md](docs/huong-dan-su-dung.md). Triển khai lên VPS: [docs/trien-khai.md](docs/trien-khai.md).

## Trạng thái triển khai

| Phase | Nội dung | Trạng thái |
|---|---|---|
| 0 | Khung dự án: module, multi-tenant RLS, outbox, web (trang công khai + 3 khu vực `learn`/`teach`/`ops`), i18n, CI | ✅ |
| 1 | Platform (tenant, host), Catalog (ngôn ngữ, khung năng lực), Identity (2 realm, tài khoản trung tâm cấp, khóa sau 5 lần sai) | ✅ — còn để sau: đăng nhập Google (cần OAuth client), xác thực tập trung `auth.konnyaku.app` (hiện đăng nhập theo từng host), 2FA cho nhân viên |
| 2 | Crm (form đăng ký công khai → lead, chống trùng & spam, gán tư vấn viên, hồ sơ học viên), Academic (chương trình theo ngôn ngữ + khung năng lực, cấp độ & bài học, lớp theo cơ sở, ghi danh giữ chỗ nguyên tử, bảo lưu/nghỉ học) | ✅ — còn để sau: đặt lịch test/học thử, giao diện tạo chương trình/lớp trong `ops` (hiện qua API) |
| 3 | Scheduling: sinh buổi học theo lịch tuần trong múi giờ của lớp (giữ đúng giờ địa phương qua DST), bỏ ngày lễ (Organization: ngày lễ theo trung tâm/cơ sở, phòng học), mỗi buổi gắn một bài học; chống trùng lịch giáo viên/phòng bằng exclusion constraint PostgreSQL (đúng cả khi đồng thời); đổi giờ, đổi giáo viên, hủy buổi; lịch dạy (`teach`) và lịch học (`learn`) | ✅ — còn để sau: availability giáo viên, thời gian di chuyển giữa cơ sở, giao diện xếp lịch trong `ops` |
| 4 | Billing: gói học (buổi trả phí + buổi tặng), đơn hàng, thanh toán nhiều lần, sổ cái buổi học append-only (DB chặn sửa/xóa), số dư theo đơn; đơn đã trả cho lớp → Worker tự ghi danh (saga Billing ↔ Academic, lớp đầy → đơn chờ hoàn tiền); trừ buổi idempotent theo điểm danh, trả phí trước rồi buổi tặng, cho phép nợ buổi; doanh thu ghi nhận theo cơ sở của lớp; portal hiển thị số buổi còn lại | ✅ — còn để sau: cổng thanh toán online, hóa đơn điện tử, hoàn tiền thực tế, chuyển gói giữa lớp |
| 5 | Meeting Engine (`Workspace`) với provider **Fake** cho dev/CI: kết nối theo tenant, pool host account, cấp host best-fit với exclusion constraint (không bao giờ một host cho 2 buổi chồng giờ, kể cả song song), tạo phòng khi buổi được xếp, đổi giờ/hủy buổi trả host, link "Vào lớp" kiểm tra quyền (học viên có chỗ trong lớp / giáo viên của buổi), endpoint mô phỏng kết thúc buổi học (người tham gia + recording) | ✅ — còn để sau: provider Google thật (cần Workspace test domain), cấp phòng theo cửa sổ T-7 ngày, job đối soát, màn hình pool trong `ops` |
| 6 | Learning: điểm danh nháp tự động từ dữ liệu người tham gia (có mặt ≥ 70%, muộn > 10 phút, < 30% là vắng; học viên bảo lưu = vắng có phép), giáo viên xác nhận (hoặc điểm danh tay cho lớp offline) → Billing trừ buổi (vắng có phép không trừ); chính sách xem lại video (mặc định mọi học viên trong lớp + giáo viên) → Workspace cấp quyền → học viên thấy trong mục xem lại; trang điểm danh trong `teach` | ✅ — còn để sau: học bù, chính sách điểm danh theo tenant, cảnh báo nghỉ học |
| 7 | Hr & payroll: đơn giá theo giờ có hiệu lực theo ngày; bảng công tự sinh — lớp online từ dữ liệu phòng học (trả phút thực dạy, tối đa bằng kế hoạch, ghi nhận số phút vào muộn), lớp offline khi giáo viên xác nhận điểm danh (trả theo kế hoạch); kỳ lương gom công nháp đến hết kỳ (kể cả công đến muộn của kỳ đã khóa), chặn kỳ chồng ngày, chốt kỳ đóng băng giá; đổi đơn giá chỉ áp cho công nháp; "Công tháng này" trong `teach`, trang "Kỳ lương" trong `ops` | ✅ — còn để sau: phụ cấp/phạt, nhiều mức giá theo vai trò/ngôn ngữ, xuất phiếu lương PDF, availability giáo viên |
| P6 | Trang công khai sẵn sàng mở ra Internet: trang 404 / lỗi riêng theo ngôn ngữ cho trang công khai và `learn`/`teach`/`ops` (có nút quay về), trang "không tìm thấy trung tâm" khi host không thuộc tenant nào; captcha proof-of-work tự host (thử thách ký HMAC có hạn dùng, lời giải dùng một lần, không gọi dịch vụ ngoài) cho form đăng ký tư vấn, cùng honeypot và giới hạn tần suất; `robots.txt` và `sitemap.xml` theo từng host, metadata / Open Graph / `hreflang` cho trang công khai, `noindex` cho khu vực đăng nhập và `/api`; trang **Chính sách quyền riêng tư** và **Điều khoản sử dụng** (bản mẫu theo Nghị định 13/2023 và Luật Bảo vệ dữ liệu cá nhân, tên trung tâm theo tenant, phiên bản khớp `consentVersion` của form) | ✅ — còn để sau: ảnh chia sẻ (Open Graph image), thông tin pháp nhân và người phụ trách dữ liệu do trung tâm tự nhập, quy trình xử lý yêu cầu xem / sửa / xóa dữ liệu (C4) |
| 8 | Màn quản trị trong `ops`: thanh điều hướng chung; Cơ sở (thêm cơ sở); Nhân sự (thêm nhân sự + vai trò, đặt đơn giá giáo viên); Chương trình (thêm chương trình, cấp độ theo khung trình độ); Lớp học (mở lớp, xếp lịch theo ngày trong tuần + giáo viên, xem buổi học và danh sách lớp); Học viên (thêm hồ sơ, cấp tài khoản đăng nhập kèm mật khẩu tạm hiện một lần); Thu phí (gói học, đơn hàng, ghi nhận thanh toán → Worker tự xếp vào lớp); Lead (chuyển trạng thái). API mới: danh sách nhân sự, gói học, đơn giá. Các form gửi qua relay `/ops/api/relay/*` có danh sách đường dẫn cho phép; API vẫn kiểm quyền | ✅ — còn để sau: đổi lịch/giáo viên từng buổi trên giao diện (API đã có), bảo lưu/nghỉ học, báo cáo doanh thu |
| 9 | Phòng học có sẵn (provider **Konnyaku**, thay Google Meet): WebRTC P2P mesh, signaling Rust (`services/signaling`, axum + WebSocket, vé JWT 2 phút do API ký, tối đa 6 người/phòng có giữ chỗ cho giáo viên), TURN coturn với mật khẩu tạm; giao diện `/room/{mã}`: kiểm tra camera/micro + chọn thiết bị, bật/tắt mic/camera, chia sẻ màn hình, chat DataChannel, giáo viên kết thúc phòng, tự kết nối lại; vào/ra báo về API → `ParticipantsCollectedV1` → điểm danh nháp + bảng công | ✅ — còn để sau: ghi hình (chưa có object storage), SFU cho lớp > 6 người, TURN qua TLS 443, nhiều bản signaling, chat lưu lại |
| 10 | Sửa dữ liệu, phân quyền theo cơ sở, lịch sử thay đổi: mọi bản ghi tạo trong `ops` sửa được (PUT) hoặc ngừng dùng (không xoá cứng khi đã được tham chiếu; ngày nghỉ và đơn giá nhập nhầm xoá được) — lớp (khoá cấp độ/cơ sở/ngày khai giảng khi đã xếp lịch, sĩ số không dưới số đã ghi danh, kết thúc/huỷ/mở lại), lịch tuần (xếp lại các buổi chưa diễn ra trong một transaction dưới exclusion constraint, Workspace trả/cấp lại host qua `SessionRescheduledV1`/`SessionCancelledV1`), chương trình, cấp độ, cơ sở, phòng, ngày nghỉ, nhân sự và vai trò, đơn giá, học viên, lead, gói học, đơn hàng, thanh toán. Ma trận quyền kiểm tra ở API theo vai trò + cơ sở, đọc trực tiếp `role_assignment` (vai trò BranchManager/Coordinator gán theo cơ sở; khoá tài khoản có hiệu lực ngay): TenantAdmin mọi thứ, BranchManager/Coordinator lớp, lịch, phòng, ngày nghỉ, ghi danh của cơ sở mình và hồ sơ học viên (BranchManager thêm lead của cơ sở mình), Accountant gói học/đơn/thanh toán/đơn giá, Consultant lead/học viên; quyền đọc cũng siết: Teacher chỉ thấy lớp mình dạy, không xem danh sách học viên, lead, nhân sự; `GET /api/identity/me/access` cho UI ẩn thao tác. Lịch sử thay đổi: bảng `change_log` mỗi module (RLS, chỉ thêm), ghi tự động trong cùng transaction (ai, lúc nào, trường, giá trị cũ/mới), xem ở `GET /api/<module>/history/{id}` và trong `ops` | ✅ — còn để sau: nhóm cơ sở (`BranchGroup`) trong phân quyền, sửa nội dung từng bài học của cấp độ, sửa ngày lễ tự dời buổi đã xếp, phân trang lịch sử (hiện 200 dòng mới nhất) |
| P1 | Notification: hàng đợi thông báo trong DB (người nhận, kênh, mẫu, locale, dữ liệu, trạng thái, số lần thử, khóa chống trùng theo tenant), Worker gửi với retry có backoff, lỗi vĩnh viễn dừng và ghi lý do; mẫu `(key, kênh, locale)` vi/en dạng file (HTML + text, tên trung tâm); email qua SMTP sau interface `IEmailSender` / `INotificationChannel` (dev: Mailpit); nghe `LeadCreatedV1`, `LearnerAccountCreatedV1`, `CreditLowV1`, `EnrollmentActivatedV1`, `SessionRescheduledV1`, `SessionCancelledV1`, `PayrollPeriodLockedV1` + nhắc lịch 24 giờ cho học viên và giáo viên; API dùng chung `INotificationApi` / event `NotificationRequestedV1`; `/ops/notifications`: nhật ký, lọc, gửi lại (TenantAdmin) | ✅ — còn để sau: Zalo ZNS / thông báo đẩy (chỉ cần thêm kênh), trung tâm tự sửa mẫu và logo/màu trong email, tùy chọn tắt từng loại thông báo, email cho phụ huynh, chốt kỳ lương chỉ báo giáo viên có công trong kỳ (cần `IHrApi`), webhook bounce của nhà cung cấp |

## Tài liệu phân tích

| Chương | Nội dung |
|---|---|
| [1. Mở rộng nghiệp vụ](docs/01-product-expansion.md) | 8 tính năng đột phá, 17 pain point vận hành, rủi ro Host Account Pooling |
| [2. Kiến trúc](docs/02-architecture.md) | Modular Monolith, Meeting Engine (sequence diagram), async/rate limit, điểm cần xác minh trong spike |
| [3. Dữ liệu lõi](docs/03-core-database.md) | ERD, bảng Session/ResourceBooking/HostLease/Ledger/Attendance/Timesheet, múi giờ, audit, concurrency |
| [4. Lộ trình](docs/04-roadmap.md) | **Nguồn duy nhất** về phạm vi V1.0 MVP → V2.0 Scale-up/SaaS → V3.0 AI, mục tiêu + Definition of Done |
| [5. Scaffolding](docs/05-scaffolding.md) | Cấu trúc thư mục BE/FE, NuGet/npm packages, ADR & checklist ngày đầu |
| [6. Tiến hóa kiến trúc](docs/06-evolution-strategy.md) | **Nguồn duy nhất** về danh sách module & event; 10 đường cắt, tách theo tenant (Silo), tiêu chí & playbook tách service |
| [7. Đa trung tâm, đa cơ sở, đa ngôn ngữ](docs/07-multi-tenant-multi-language.md) | **Nguồn duy nhất** về quy tắc đa tenant; identity xuyên tenant, RLS, cấu hình kế thừa, khung năng lực, i18n, đăng nhập 2 tab & trang công khai |
| [8. Đề xuất cải tiến](docs/08-de-xuat-cai-tien.md) | Việc nên làm tiếp (11/2026), xếp theo giá trị và công sức, để chọn đưa vào roadmap |
| [9. Rà soát và kế hoạch production](docs/09-ra-soat-va-ke-hoach-production.md) | Đủ / thiếu theo từng vai trò, lỗi cần sửa ngay, khoảng cách tới chuẩn production, kế hoạch 4 giai đoạn |

## Thuật ngữ

| Từ | Nghĩa |
|---|---|
| **Tenant** / trung tâm | Một khách hàng của Konnyaku (một pháp nhân, đơn vị tài chính) |
| **Cơ sở** (branch) | Địa điểm học của một trung tâm, kể cả cơ sở ảo "Online" |
| **GV** / **HV** | Giáo viên / Học viên |
| Module **`Workspace`** | Meeting Engine của Konnyaku (host pool, phòng Meet, recording) — khác *Google Workspace* (sản phẩm của Google) |
| **Phòng học có sẵn** / provider `Konnyaku` | Phòng học WebRTC của chính Konnyaku (`/room/{mã}` + `services/signaling`), dùng khi không có Google Meet |
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
