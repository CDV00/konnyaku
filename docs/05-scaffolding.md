# Chương 5 — Bộ khung Khởi tạo Dự án

## 5.1 Monorepo tổng

```
konnyaku/
├── backend/                      .NET solution (Modular Monolith)
├── frontend/                     Next.js (pnpm workspace: 4 app + packages dùng chung)
├── ai/                           (V3) Python service nếu cần model tự host
├── infra/
│   ├── docker/                   docker-compose: postgres, redis, minio, pubsub-emulator, mailpit
│   ├── terraform/                GCP project, Pub/Sub topic/subscription, service account
│   └── k8s/ hoặc fly/ ...
├── docs/                         tài liệu này + ADR (Architecture Decision Records)
│   └── adr/0001-modular-monolith.md, 0002-postgresql.md, ...
├── .github/workflows/            CI: build, test, architecture test, OpenAPI → client codegen
└── README.md
```

## 5.2 Backend — .NET Clean Architecture theo module

```
backend/
├── Konnyaku.sln
├── Directory.Build.props              Nullable, TreatWarningsAsErrors, LangVersion
├── Directory.Packages.props           Central Package Management — 1 nơi khai version
├── src/
│   ├── Hosts/
│   │   ├── Konnyaku.Api/              Program.cs, middleware, OpenAPI, auth — chỉ "lắp ráp" module
│   │   └── Konnyaku.Worker/           Hangfire server, Outbox dispatcher, Pub/Sub consumer
│   ├── BuildingBlocks/
│   │   ├── Konnyaku.SharedKernel/     Entity, AggregateRoot, ValueObject, Result<T>, DomainEvent,
│   │   │                              TenantId, BranchId, ITenantOwned, Money, LocalizedString, LanguageCode (BCP 47)
│   │   ├── Konnyaku.Application/      ICommandHandler/IQueryHandler, pipeline (validation, tx, logging),
│   │   │                              ITenantContext, IScopeAuthorizer (tenant/nhóm cơ sở/cơ sở), ISettingProvider
│   │   └── Konnyaku.Infrastructure/   EF Core base DbContext (tenant query filter + RLS `SET LOCAL`),
│   │                                  Outbox/Inbox, audit interceptor, Clock (NodaTime IClock),
│   │                                  Redis lock, rate limiter + fair-share theo tenant, server-side localization
│   └── Modules/
│       ├── Platform/                  Tenant, domain, gói dịch vụ, feature flag, setting kế thừa, sequence
│       ├── Organization/              Branch, BranchGroup, Room, lịch nghỉ, thời gian di chuyển
│       ├── Catalog/                   Ngôn ngữ, khung năng lực, level, kỹ năng (dữ liệu chuẩn nền tảng)
│       ├── Academic/
│       │   ├── Konnyaku.Academic.Domain/          Class, Enrollment, Curriculum — không phụ thuộc gì
│       │   ├── Konnyaku.Academic.Application/     Commands/Queries/Handlers/Validators
│       │   ├── Konnyaku.Academic.Infrastructure/  AcademicDbContext (schema "academic"), repositories
│       │   ├── Konnyaku.Academic.Endpoints/       Minimal API endpoints của module
│       │   └── Konnyaku.Academic.Contracts/       Public API cho module khác + integration events
│       ├── Scheduling/        (cùng cấu trúc 5 project)
│       ├── Billing/
│       ├── Crm/
│       ├── Hr/
│       ├── Learning/
│       ├── Notification/
│       ├── Identity/
│       └── Workspace/                 Google Workspace Engine
│           ├── Konnyaku.Workspace.Domain/          HostAccount, HostLease, MeetingSpace, Recording
│           ├── Konnyaku.Workspace.Application/     AcquireLease, ProvisionMeeting, PublishRecording
│           ├── Konnyaku.Workspace.Infrastructure/
│           │   ├── Google/                         GoogleCredentialFactory (DWD impersonation),
│           │   │                                   MeetClient, CalendarClient, DriveClient
│           │   ├── Events/                         Pub/Sub consumer, subscription renewer
│           │   └── Providers/GoogleMeetRoomProvider.cs   : IVirtualRoomProvider
│           ├── Konnyaku.Workspace.Endpoints/
│           └── Konnyaku.Workspace.Contracts/
└── tests/
    ├── Konnyaku.ArchitectureTests/    NetArchTest: module A không tham chiếu Domain module B
    ├── Konnyaku.<Module>.UnitTests/
    ├── Konnyaku.IntegrationTests/     Testcontainers PostgreSQL thật (exclusion constraint phải test trên DB thật)
    ├── Konnyaku.TenantIsolationTests/ Mọi bảng có tenant_id phải bật RLS; cùng truy vấn dưới 2 tenant không thấy dữ liệu của nhau
    └── Konnyaku.Workspace.ContractTests/   WireMock giả lập Google API: 429, 5xx, event trùng
```

Quy ước: **vertical slice trong Application** (mỗi use case 1 thư mục: `ScheduleSession/ScheduleSessionCommand.cs`, `Handler.cs`, `Validator.cs`, `Endpoint.cs`) — dễ tìm hơn chia theo tầng kỹ thuật.

## 5.3 Frontend — Next.js App Router

Bốn ứng dụng cho bốn nhóm nhu cầu rất khác nhau (khách vãng lai, nhân viên, giáo viên, học viên/phụ huynh — khác mật độ dữ liệu, thiết bị, bảo mật), chung design system:

```
frontend/
├── pnpm-workspace.yaml
├── apps/
│   ├── site/                          Trang công khai của trung tâm + trang đăng nhập 2 tab (SSR/SSG, SEO)
│   │   └── app/
│   │       ├── [locale]/{page.tsx, courses/, schedule/, teachers/, register/}   (đường dẫn hiển thị được dịch theo locale)
│   │       └── login/                 tab Học viên·Phụ huynh → realm learn | tab Giáo viên·Nhân viên → realm staff
│   ├── ops/                           Admin / Quản lý cơ sở / Điều phối / Tư vấn / Kế toán (desktop, data-dense)
│   │   └── app/
│   │       ├── (dashboard)/
│   │       │   ├── layout.tsx         sidebar + RBAC guard
│   │       │   ├── classes/[classId]/{page.tsx, sessions/, enrollments/}
│   │       │   ├── schedule/          calendar view theo GV / phòng / host pool
│   │       │   ├── students/[studentId]/{profile, ledger, attendance}
│   │       │   ├── billing/{orders, invoices, refunds}
│   │       │   ├── teachers/[teacherId]/{availability, timesheets}
│   │       │   ├── payroll/[periodId]/
│   │       │   ├── meetings/{host-pool, leases, recordings-health}
│   │       │   └── website/           nội dung trang công khai
│   │       └── api/                   route handlers tối thiểu (BFF/proxy nếu cần)
│   ├── teach/                         App giáo viên (mobile-first): lịch dạy, vào lớp, duyệt điểm danh, feedback
│   │   └── app/join/[sessionId]/route.ts   → /teach/join/{id}
│   └── learn/                         Portal học viên & phụ huynh (PWA): lịch, "Vào lớp", xem lại, học bù, số dư buổi
│       └── app/join/[sessionId]/route.ts   → /learn/join/{id}: kiểm tra quyền → redirect meetingUri
├── packages/
│   ├── ui/                            design system Konnyaku (shadcn/ui + Tailwind tokens)
│   ├── api-client/                    sinh tự động từ OpenAPI của Konnyaku.Api (orval) + hooks TanStack Query
│   ├── auth/                          cấu hình Auth.js dùng chung
│   ├── i18n/                          messages/{locale}/{namespace}.json (ICU), locale matcher,
│   │                                  lint chặn chuỗi cứng; V1: vi, en; (V2) ghi đè thuật ngữ theo tenant
│   ├── datetime/                      helper hiển thị theo timezone người xem — **cấm** dùng `new Date().toLocaleString` rải rác
│   └── config/                        eslint, tsconfig, tailwind preset
└── e2e/                               Playwright
```

Bốn app chạy chung một domain của tenant theo đường dẫn (`/`, `/learn`, `/teach`, `/ops`) bằng **Next.js Multi-Zones** — mỗi app build/deploy độc lập nhưng người dùng thấy một website. Realm `learn` và `staff` dùng cookie khác tên và token khác `aud` (xem [7.9.2](07-multi-tenant-multi-language.md#792-trang-đăng-nhập-hai-tab--tách-hẳn-hai-cõi-realm)).

Đa tenant ở FE: middleware Next.js đọc host (`{tenant}.konnyaku.app` hoặc custom domain) → nạp thương hiệu và danh sách locale tenant cho phép. Đăng nhập đi qua `auth.konnyaku.app` rồi quay về host của tenant; người có nhiều membership đổi trung tâm bằng cách chuyển sang host của trung tâm kia ([7.3](07-multi-tenant-multi-language.md#73-định-danh-xuyên-tenant)). Trang công khai của tenant (đăng ký học thử, placement test) nằm dưới `app/[locale]/...` để locale có trong URL; trang sau đăng nhập lấy locale từ hồ sơ user.

Quy ước trong mỗi route: `page.tsx` (Server Component lấy dữ liệu ban đầu) → `_components/` (Client Components) → `_hooks/` → dữ liệu gọi qua `@konnyaku/api-client`, không gọi `fetch` tay.

## 5.4 Thư viện thiết yếu

### Backend (NuGet)
| Mục đích | Package | Ghi chú |
|---|---|---|
| Runtime | .NET 10 (LTS), ASP.NET Core Minimal APIs | |
| ORM | `Microsoft.EntityFrameworkCore`, `Npgsql.EntityFrameworkCore.PostgreSQL`, `EFCore.NamingConventions` (snake_case) | |
| Thời gian | `NodaTime`, `Npgsql.EntityFrameworkCore.PostgreSQL.NodaTime` | Bắt buộc cho bài toán múi giờ |
| Lặp lịch | `Ical.Net` | Parse/expand RRULE |
| Background jobs | `Hangfire.AspNetCore`, `Hangfire.PostgreSql` | Dashboard sẵn, retry, queue ưu tiên. (Quartz.NET nếu cần lịch cron phức tạp, không cần cả hai) |
| Google | `Google.Apis.Calendar.v3`, `Google.Apis.Drive.v3`, `Google.Apis.Admin.Directory.directory_v1`, `Google.Apps.Meet.V2`, `Google.Apps.Events.Subscriptions.V1`, `Google.Cloud.PubSub.V1` | Kiểm tra trạng thái GA/beta của Meet & Events client tại thời điểm cài |
| Resilience | `Microsoft.Extensions.Http.Resilience` (Polly v8) | Retry/backoff/circuit breaker |
| Validation | `FluentValidation` | |
| Mapping | `Riok.Mapperly` | Source generator, không reflection |
| Messaging nội bộ | Tự viết dispatcher mỏng, hoặc `WolverineFx` | Xem lưu ý license bên dưới |
| Cache / lock | `StackExchange.Redis`, `ZiggyCreatures.FusionCache` | |
| Auth | `Microsoft.AspNetCore.Authentication.JwtBearer`, `OpenIddict` cho `auth.konnyaku.app` (khuyến nghị) | Phương án thay thế: Keycloak/Auth0 — chốt bằng ADR; lưu ý chữ "realm" của Keycloak khác nghĩa realm `learn`/`staff` trong tài liệu này |
| Logging/Tracing | `Serilog.AspNetCore`, `OpenTelemetry.Extensions.Hosting` + exporters | |
| OpenAPI | `Microsoft.AspNetCore.OpenApi`, `Scalar.AspNetCore` | Sinh client FE |
| Excel/PDF | `ClosedXML`, `QuestPDF` | Payslip, báo cáo (kiểm tra điều kiện license QuestPDF theo doanh thu) |
| Đa ngôn ngữ phía server | `Microsoft.Extensions.Localization` hoặc đọc chung catalog ICU JSON với FE (`MessageFormat` cho .NET) | Email/PDF/thông báo theo locale người nhận |
| Test | `xunit.v3`, `Testcontainers.PostgreSql`, `Respawn`, `WireMock.Net`, `NetArchTest.Rules`, `NSubstitute`, `Shouldly` | |

> ⚠️ **Lưu ý license (2025)**: MediatR, AutoMapper, MassTransit v9 và FluentAssertions v8 đã chuyển sang mô hình thương mại. Không cần chúng — dùng Mapperly, dispatcher tự viết/Wolverine, Shouldly — hoặc ghim phiên bản OSS cũ có ý thức. Ghi quyết định vào ADR.

### Frontend (npm)
| Mục đích | Package |
|---|---|
| Framework | `next`, `react`, `react-dom`, `typescript` |
| Data fetching | `@tanstack/react-query`, `orval` (codegen từ OpenAPI) |
| Bảng dữ liệu | `@tanstack/react-table`, `@tanstack/react-virtual` |
| Form | `react-hook-form`, `zod`, `@hookform/resolvers` |
| UI | `tailwindcss`, shadcn/ui (`@radix-ui/*`), `lucide-react`, `sonner` (toast), `cmdk` |
| Lịch | `@fullcalendar/react` + `resource-timeline` (xem lịch theo GV/phòng/host — lưu ý gói premium có license) hoặc `@schedule-x/react` |
| Thời gian | `date-fns`, `@date-fns/tz` |
| State client | `zustand` (chỉ cho UI state, dữ liệu server để React Query giữ) |
| i18n | `next-intl` (ICU MessageFormat), `@formatjs/intl-localematcher`, `eslint-plugin-i18next` (chặn chuỗi cứng trong JSX); quản lý bản dịch: Tolgee tự host hoặc Crowdin |
| Font đa ngữ | `next/font` với Noto Sans (vi) + Noto Sans JP/SC/KR tải theo locale |
| Auth | Client OIDC tới `auth.konnyaku.app` (vd `openid-client`), hoặc `next-auth` (Auth.js) — kiểm tra trạng thái bảo trì của Auth.js tại thời điểm chọn |
| Biểu đồ | `recharts` |
| PWA (learn app) | `@serwist/next` |
| Chất lượng | `eslint`, `prettier`, `vitest`, `@testing-library/react`, `@playwright/test`, `msw` |

## 5.5 Checklist ngày đầu tiên
1. `docker compose up` có: PostgreSQL (bật `btree_gist`, `pg_trgm`, ICU collation), Redis, MinIO, Pub/Sub emulator, Mailpit. Seed sẵn 2 tenant giả, mỗi tenant 2 cơ sở, 2 ngôn ngữ giảng dạy — để mọi dev luôn chạy trong điều kiện đa tenant.
2. Tạo GCP project riêng cho dev, ứng dụng được cấp quyền trên **Workspace test domain** (không dùng domain production để thử); không tạo key file cho service account.
3. CI chạy: build → unit → integration (Testcontainers) → architecture tests → sinh OpenAPI → sinh `api-client` → typecheck FE.
4. ADR đầu tiên: Modular Monolith & 10 đường cắt, PostgreSQL, multi-tenant Pool + RLS, NodaTime/UTC, credit ledger append-only, `IVirtualRoomProvider`, identity provider & hai realm đăng nhập, cách ủy quyền Google Workspace (Marketplace app / không key file), i18n (ICU + `LocalizedString`).
5. **Spike 1 tuần trước khi code tính năng**: chứng minh end-to-end trên domain test — tạo space bằng account impersonate → ghi hình tự động → nhận event `recording.fileGenerated` → chuyển sang Shared Drive → cấp quyền cho 1 email ngoài; kiểm tra đủ các điểm ở [2.3.4](02-architecture.md#234-điểm-then-chốt-trong-luồng) mục 9. Kết quả spike quyết định phạm vi V1.
