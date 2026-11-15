---
name: kk-backend-module
description: Tạo module backend mới hoặc thêm/sửa bảng trong module Konnyaku — DbContext theo schema, migration EF, Row-Level Security theo tenant, role PostgreSQL riêng, connection string, đăng ký IModule. Dùng khi đổi entity cấu hình EF, chạy dotnet ef migrations, hoặc thêm module vào KonnyakuModules.
---

# Module và dữ liệu

Mỗi module = một schema PostgreSQL + một role `kk_<module>` chỉ có quyền DML trên schema đó, không bypass RLS. Mẫu: `backend/src/Modules/Hr/`.

## Thêm bảng vào module có sẵn

1. Entity trong `Domain`; bảng theo tenant implement `ITenantOwned` (`ModuleDbContext` tự gán `TenantId`, thêm query filter, chặn đổi tenant).
2. Cấu hình trong `<M>DbContext.ConfigureModel`: `ToTable("snake_case")` (đã có `UseSnakeCaseNamingConvention`), enum lưu `HasConversion<string>().HasMaxLength(20)`, tiền `HasPrecision(18, 2)`, index bắt đầu bằng `TenantId`. Thêm `DbSet` vào cả `I<M>DbContext` ở Application.
3. Tạo migration (dùng design-time factory có sẵn trong Infrastructure):

   ```bash
   cd backend && dotnet tool restore && dotnet ef migrations add <TenMigration> --project src/Modules/<M>/Konnyaku.<M>.Infrastructure --context <M>DbContext --output-dir Migrations
   ```

4. Mở file migration vừa sinh, thêm cuối `Up`: `migrationBuilder.EnableTenantRls("<schema>", "<table>");` và trong `Down`: `migrationBuilder.DisableTenantRls(...)`. Thiếu bước này thì dữ liệu tenant không được cách ly ở DB (`TenantIsolationTests`).
5. Ràng buộc chống chồng thời gian (giáo viên, phòng, host) viết bằng SQL trong migration: exclusion constraint `tstzrange` + `btree_gist` (xem migration của `Scheduling`, `Workspace`).
6. Bảng append-only (như `billing.credit_ledger_entry`): trong migration `REVOKE UPDATE, DELETE ON <schema>.<table> FROM kk_<module>;` để role của module chỉ được thêm và đọc.
7. Áp dụng: `cd backend && dotnet run --project src/Hosts/Konnyaku.Migrator`.

## Tạo module mới

1. Tạo 5 project `Konnyaku.<M>.{Domain,Application,Infrastructure,Endpoints,Contracts}` dưới `src/Modules/<M>/`, thêm vào `Konnyaku.slnx`, chép cấu trúc csproj từ module Hr (package version nằm ở `Directory.Packages.props`).
2. Contracts chỉ phụ thuộc `Konnyaku.SharedKernel` (interface `I<M>Api`, DTO, event `...V1`).
3. `<M>DbContext : ModuleDbContext`, `SchemaName` = tên schema; extension `Add<M>Infrastructure` gọi `AddModuleDbContext<...>(configuration, connectionName, SchemaName)`; design-time factory như `HrDesignTimeFactory` (bảng lịch sử `__ef_migrations_history` trong schema module).
4. `<M>Module : IModule` (`Name`, `DbContextTypes`, `AddServices`, `AddWorkerServices` → `services.AddOutboxProcessor<<M>DbContext>()`, `MapEndpoints`).
5. Đăng ký `new <M>Module()` trong `src/Hosts/Konnyaku.Composition/KonnyakuModules.cs`.
6. Thêm dòng `('<module>', '<schema>')` vào `infra/docker/postgres/init/01-modules.sql`, connection string `"<module>": "...Username=kk_<module>;Password=kk_<module>_dev"` vào `appsettings.Development.json` của Api và Worker (Migrator dùng chuỗi `Owner` chung, không cần thêm). Dev phải tạo lại volume: `docker compose -f infra/docker/docker-compose.yml down -v && ... up -d`.
7. Cập nhật danh sách module trong `docs/06-evolution-strategy.md` nếu module là mới hoàn toàn.

## Kiểm tra

- `dotnet test --solution Konnyaku.slnx` (ArchitectureTests kiểm ranh giới; IntegrationTests chạy migration thật trên Testcontainers).
- Không bao giờ sửa migration đã merge vào `main`; tạo migration mới.
