# Konnyaku — context cho Claude Code và người mới

SaaS Education ERP + LMS cho trung tâm ngoại ngữ: nhiều trung tâm (tenant), mỗi trung tâm nhiều cơ sở, dạy nhiều ngôn ngữ, giao diện `vi` / `en`. Học online qua Google Meet (dev dùng provider **Fake**), mở rộng Hybrid / tại cơ sở.

Đọc thêm: [README.md](README.md) (chạy local, trạng thái từng phase, thuật ngữ, quyết định chính) và `docs/` (phân tích; `04-roadmap`, `06-evolution-strategy`, `07-multi-tenant-multi-language` là nguồn duy nhất cho phạm vi, module/event và quy tắc đa tenant). Hướng dẫn người dùng cuối: [docs/huong-dan-su-dung.md](docs/huong-dan-su-dung.md).

## Bố cục

| Đường dẫn | Nội dung |
| --- | --- |
| `backend/` | .NET 10, `Konnyaku.slnx`. Modular monolith: 2 process `Api` (5080) và `Worker` (5090) từ một solution, `Migrator` chạy migration |
| `backend/src/Modules/<M>/` | Mỗi module 5 project: `Domain`, `Application`, `Infrastructure`, `Endpoints`, `Contracts` |
| `backend/src/BuildingBlocks/` | `SharedKernel` (Entity, `ITenantOwned`, `Result<T>`, `Error`, `LocalizedString`), `Application`, `Infrastructure` (ModuleDbContext, outbox/inbox, auth, `ApiResults`, RLS helpers) |
| `backend/src/Hosts/Konnyaku.Composition/KonnyakuModules.cs` | Danh sách module đăng ký |
| `backend/tests/` | `UnitTests`, `IntegrationTests` (Testcontainers PostgreSQL, cần Docker), `ArchitectureTests` (ranh giới module + snapshot contracts) |
| `frontend/` | pnpm workspace. Một app Next.js 16 `apps/site` (cổng 4000): trang công khai + login, và 3 khu vực `app/learn`, `app/teach`, `app/ops` (mỗi khu vực có root layout riêng) |
| `frontend/packages/` | `i18n` (catalog `messages/{vi,en}/*.json`), `session` (đăng nhập, `requireMe`, `apiFetch`, `ZoneLayout`, lịch), `ui` (`@konnyaku/ui`: Tailwind v4, animate-ui + shadcn vendored, khối riêng `src/kk`) |
| `infra/` | `docker/docker-compose.yml` (PostgreSQL cổng 5433), `postgres/init/01-modules.sql` (schema + role mỗi module), `scripts/seed_demo.py` |

## Quy tắc không được phá

1. **Ranh giới module**: module khác chỉ được tham chiếu `Konnyaku.<Other>.Contracts` (interface `I<Other>Api` + DTO + integration event). Không JOIN, FK hay transaction chéo module. `ArchitectureTests` kiểm tra điều này.
2. **Multi-tenant fail-closed**: mọi bảng nghiệp vụ có `tenant_id`, entity implement `ITenantOwned` (EF tự gán và lọc), migration bật RLS bằng `EnableTenantRls`. Mỗi module dùng role DB riêng `kk_<module>`, chỉ có quyền trên schema của mình.
3. **Giao tiếp bất đồng bộ qua outbox**: `db.Publish(new XxxV1(...))` rồi `SaveChangesAsync` trong cùng transaction; Worker phát đi, inbox chống xử lý trùng. Event là `sealed record ...V1` trong Contracts; đổi phá vỡ thì thêm `V2`, không sửa `V1`.
4. **Lỗi trả về là mã**: `Error.Validation/Conflict/NotFound("<module>.<snake_case>")` → body `{ code, params }`. Frontend đổi `.` thành `_` rồi dịch trong catalog i18n (`errors.hr_period_overlaps`). Không trả câu chữ tiếng Việt/Anh từ API.
5. **Không chuỗi cứng trong UI**: ESLint `react/jsx-no-literals` bật; mọi chữ lấy từ `packages/i18n/messages`, luôn thêm cả `vi` và `en` (kiểu `Messages` suy từ `vi`).
6. **Thời gian**: lưu UTC, lịch neo theo múi giờ IANA của lớp/cơ sở (`TimeZoneInfo`, giữ đúng giờ địa phương qua DST). Chống trùng lịch GV/phòng/host bằng exclusion constraint PostgreSQL, không kiểm tra bằng code.
7. **Sổ buổi học append-only** (`Billing`): không sửa/xoá dòng ledger, chỉ ghi bút toán mới.
8. **Dữ liệu test và seed** dùng `*@example.com`, không dùng email hay tên thật.

## Môi trường máy dev

- Node 20.18: giữ `tailwindcss` và `@tailwindcss/postcss` đúng **4.1.18** (4.3 cần `require(esm)`, lỗi build).
- Nếu `pnpm` (corepack) lỗi, dùng `npx -y pnpm@10.34.9 …`.
- Cách bật/tắt, bảng cổng, lỗi thường gặp: [docs/chay-local.md](docs/chay-local.md). Cổng: web 4000, API 5080, Worker 5090, PostgreSQL 5433.
- Dev server khai báo trong `.claude/launch.json` (api, worker, site).
- Tenant xác định theo host: mở `http://demo.localhost:4000`. Tài khoản demo trong `docs/chay-local.md`.

## Skill dùng chung

Các quy trình lặp lại nằm trong `.claude/skills/` (Claude Code tự nạp; người đọc cũng dùng được như checklist):

| Skill | Khi nào dùng |
| --- | --- |
| `kk-backend-feature` | Thêm/sửa endpoint, command, query trong module có sẵn |
| `kk-backend-module` | Tạo module mới hoặc bảng mới (DbContext, migration, RLS, role DB) |
| `kk-integration-event` | Phát / tiêu thụ event giữa các module |
| `kk-frontend-screen` | Thêm màn hình, form, chuỗi i18n trong `site`/`learn`/`teach`/`ops` |
| `kk-ui-component` | Thêm component animate-ui/shadcn vào `@konnyaku/ui` |
| `kk-verify` | Chạy kiểm tra trước khi commit (build, test, lint, typecheck) và kiểm tra trên trình duyệt |
| `kk-git-workflow` | Nhánh, commit, cập nhật README, merge |
