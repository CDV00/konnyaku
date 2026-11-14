# Chương 5 — Bộ khung Khởi tạo Dự án

## 5.1 Monorepo tổng

```
konnyaku/
├── backend/                      .NET solution (Modular Monolith)
├── frontend/                     Next.js (pnpm workspace: 3 app + packages dùng chung)
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
│   │   ├── Konnyaku.SharedKernel/     Entity, AggregateRoot, ValueObject, Result<T>, DomainEvent, TenantId
│   │   ├── Konnyaku.Application/      ICommandHandler/IQueryHandler, pipeline (validation, tx, logging)
│   │   └── Konnyaku.Infrastructure/   EF Core base DbContext, Outbox/Inbox, audit interceptor,
│   │                                  Clock (NodaTime IClock), Redis lock, rate limiter
│   └── Modules/
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
    └── Konnyaku.Workspace.ContractTests/   WireMock giả lập Google API: 429, 5xx, event trùng
```

Quy ước: **vertical slice trong Application** (mỗi use case 1 thư mục: `ScheduleSession/ScheduleSessionCommand.cs`, `Handler.cs`, `Validator.cs`, `Endpoint.cs`) — dễ tìm hơn chia theo tầng kỹ thuật.

## 5.3 Frontend — Next.js App Router

Ba ứng dụng cho ba nhóm người dùng có nhu cầu rất khác nhau (mật độ dữ liệu, thiết bị, bảo mật), chung design system:

```
frontend/
├── pnpm-workspace.yaml
├── apps/
│   ├── ops/                           Admin / Điều phối / Tư vấn / Kế toán (desktop, data-dense)
│   │   └── app/
│   │       ├── (auth)/login/
│   │       ├── (dashboard)/
│   │       │   ├── layout.tsx         sidebar + RBAC guard
│   │       │   ├── classes/[classId]/{page.tsx, sessions/, enrollments/}
│   │       │   ├── schedule/          calendar view theo GV / phòng / host pool
│   │       │   ├── students/[studentId]/{profile, ledger, attendance}
│   │       │   ├── billing/{orders, invoices, refunds}
│   │       │   ├── teachers/[teacherId]/{availability, timesheets}
│   │       │   ├── payroll/[periodId]/
│   │       │   └── workspace/{host-pool, leases, recordings-health}
│   │       └── api/                   route handlers tối thiểu (BFF/proxy nếu cần)
│   ├── teach/                         Teacher Workspace (mobile-first): lịch dạy, vào lớp, duyệt điểm danh, feedback
│   └── learn/                         Student & Parent Portal (PWA): lịch, "Vào lớp", xem lại, học bù, số dư buổi
│       └── app/join/[sessionId]/route.ts   kiểm tra quyền → redirect meetingUri
├── packages/
│   ├── ui/                            design system Konnyaku (shadcn/ui + Tailwind tokens)
│   ├── api-client/                    sinh tự động từ OpenAPI của Konnyaku.Api (orval) + hooks TanStack Query
│   ├── auth/                          cấu hình Auth.js dùng chung
│   ├── i18n/                          vi / en (GV nước ngoài dùng tiếng Anh)
│   ├── datetime/                      helper hiển thị theo timezone người xem — **cấm** dùng `new Date().toLocaleString` rải rác
│   └── config/                        eslint, tsconfig, tailwind preset
└── e2e/                               Playwright
```

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
| Auth | `Microsoft.AspNetCore.Authentication.JwtBearer`, `OpenIddict` (nếu tự làm IdP) | Hoặc Keycloak/Auth0 bên ngoài |
| Logging/Tracing | `Serilog.AspNetCore`, `OpenTelemetry.Extensions.Hosting` + exporters | |
| OpenAPI | `Microsoft.AspNetCore.OpenApi`, `Scalar.AspNetCore` | Sinh client FE |
| Excel/PDF | `ClosedXML`, `QuestPDF` | Payslip, báo cáo (kiểm tra điều kiện license QuestPDF theo doanh thu) |
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
| i18n | `next-intl` |
| Auth | `next-auth` (Auth.js) |
| Biểu đồ | `recharts` |
| PWA (learn app) | `@serwist/next` |
| Chất lượng | `eslint`, `prettier`, `vitest`, `@testing-library/react`, `@playwright/test`, `msw` |

## 5.5 Checklist ngày đầu tiên
1. `docker compose up` có: PostgreSQL (bật `btree_gist`), Redis, MinIO, Pub/Sub emulator, Mailpit.
2. Tạo GCP project riêng cho dev, service account + domain-wide delegation trên **Workspace test domain** (không dùng domain production để thử).
3. CI chạy: build → unit → integration (Testcontainers) → architecture tests → sinh OpenAPI → sinh `api-client` → typecheck FE.
4. ADR đầu tiên: Modular Monolith, PostgreSQL, NodaTime/UTC, credit ledger append-only, `IVirtualRoomProvider`.
5. **Spike 1 tuần trước khi code tính năng**: chứng minh end-to-end trên domain test — tạo space bằng account impersonate → auto-record → nhận event `recording.fileGenerated` → move sang Shared Drive → share cho 1 email ngoài. Kết quả spike quyết định phạm vi V1.
