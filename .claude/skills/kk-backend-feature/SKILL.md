---
name: kk-backend-feature
description: Thêm hoặc sửa endpoint, command, query trong một module backend Konnyaku đã có (Minimal API + handler trả Result<T> + mã lỗi + test tích hợp). Dùng khi làm việc trong backend/src/Modules/*/ với *Module.cs, *Handlers.cs, *Queries, hoặc khi thêm API mới cho frontend.
---

# Thêm tính năng backend trong module có sẵn

Ví dụ mẫu đáng đọc trước: `backend/src/Modules/Hr/` (nhỏ, đủ các mẫu) và `tests/Konnyaku.IntegrationTests/PayrollTests.cs`.

## Các bước

1. **Domain** (`Konnyaku.<M>.Domain`): logic nghiệp vụ nằm trên entity (`Timesheet.RecordMeeting`, `PayrollPeriod.Lock`), không phụ thuộc EF/ASP.NET. Entity kế thừa `Entity` (Id là UUID v7), bảng theo tenant implement `ITenantOwned`.
2. **Application** (`Konnyaku.<M>.Application/<M>Handlers.cs`):
   - Command/view là `sealed record` (`SetRateCommand`, `RateView`).
   - Method `async Task<Result<TView>> XxxAsync(..., CancellationToken ct)`; lỗi trả `Error.Validation|Conflict|NotFound("<module>.<snake_case>")`, không throw cho lỗi nghiệp vụ.
   - Truy cập DB qua interface `I<M>DbContext` (DbSet + `Publish` + `SaveChangesAsync`), không dùng DbContext cụ thể.
   - Ràng buộc DB (unique, exclusion) bắt bằng `IDbErrorClassifier.IsUniqueViolation/IsExclusionViolation` rồi đổi thành `Error.Conflict`.
   - Cần dữ liệu module khác → inject `I<Other>Api` từ `Konnyaku.<Other>.Contracts`. Không tham chiếu Domain/Application/Infrastructure của module khác (ArchitectureTests sẽ fail).
3. **Endpoints** (`Konnyaku.<M>.Endpoints/<M>Module.cs`, trong `MapEndpoints`):
   - Route tương đối; host gắn tiền tố `/api/<module>`.
   - Ghi: `ApiResults.From(await handlers.XAsync(...), Results.Ok)` hoặc `p => Results.Created($"xs/{p.Id}", p)`.
   - Phân quyền tường minh: `.RequireAuthorization(Policies.Staff)`, `Policies.Learn`, `Policies.TenantAdmin`, hoặc policy builder với `.RequireClaim(KonnyakuClaims.Audience, Audiences.Staff).RequireRole("TenantAdmin", "Accountant")`. Endpoint "của tôi" lấy `user.UserId()` từ `ClaimsPrincipal`, không nhận id từ client.
   - Đăng ký handler mới trong `AddServices` (`services.AddScoped<XHandlers>()`).
4. **Mã lỗi phía frontend**: mỗi mã mới cần chuỗi trong `frontend/packages/i18n/messages/{vi,en}/apps.json` dưới namespace màn hình dùng nó, key là mã với `.` đổi thành `_` (ví dụ `admin.errors.hr_period_overlaps`).
5. **Màn hình `ops` gọi API ghi** → thêm regex đường dẫn vào allowlist `frontend/apps/ops/app/api/relay/[...path]/route.ts`.
6. **Test**:
   - Logic thuần (chính sách, tính toán) → `tests/Konnyaku.UnitTests`.
   - Luồng API → `tests/Konnyaku.IntegrationTests`: `[Collection(PostgresCollection.Name)]`, `KonnyakuApiFactory`, `CreateTenantWithAdminAsync`, `StaffLoginAsync`, `ClientFor(slug, token)`; gọi `DrainOutboxAsync()` khi luồng đi qua event. Email test dạng `$"x-{Guid.NewGuid():N}@example.com"`.
   - Luôn thêm ít nhất một ca lỗi (403 sai vai trò, 409 trùng, tenant khác không thấy dữ liệu).
7. Thay đổi bảng → xem skill `kk-backend-module` (migration + RLS). Thay đổi DTO/event trong Contracts → xem `kk-integration-event` (snapshot).

## Kiểm tra

```bash
cd backend && dotnet build Konnyaku.slnx && dotnet test --solution Konnyaku.slnx
```

Integration test cần Docker đang chạy.
