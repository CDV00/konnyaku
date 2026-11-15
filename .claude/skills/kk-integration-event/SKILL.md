---
name: kk-integration-event
description: Phát hoặc tiêu thụ integration event giữa các module Konnyaku qua outbox (Publish + Worker + IIntegrationEventHandler), và sửa Contracts an toàn (snapshot, đánh version V1/V2). Dùng khi một thay đổi ở module này cần phản ứng ở module khác, hoặc khi test ArchitectureTests báo "Public contracts changed".
---

# Integration event

Luồng: handler ghi dữ liệu + `db.Publish(event)` trong cùng `SaveChangesAsync` → bảng `outbox_message` của module → Worker (`OutboxProcessor<TContext>`) đặt tenant của event vào `TenantContext` rồi gọi mọi `IIntegrationEventHandler<TEvent>` đã đăng ký.

## Phát event

1. Khai báo trong `Konnyaku.<M>.Contracts`: `public sealed record XxxHappenedV1(Guid ..., ...) : IIntegrationEvent;` — chỉ kiểu nguyên thủy, `Guid`, `DateOnly`, `DateTimeOffset`, record DTO cùng Contracts. Thêm doc comment một dòng nói ai dùng.
2. Trong handler: `db.Publish(new XxxHappenedV1(...));` trước `SaveChangesAsync`. Ngoài ngữ cảnh tenant hiện tại (job nền) dùng `PublishFor(tenantId, ...)`.
3. Module phát phải có `AddWorkerServices => services.AddOutboxProcessor<<M>DbContext>()`.

## Tiêu thụ event

1. Lớp `public sealed class XxxConsumer(...) : IIntegrationEventHandler<XxxHappenedV1>` trong Application của module nhận.
2. Đăng ký trong `<M>Module.AddServices`: `services.AddScoped<IIntegrationEventHandler<XxxHappenedV1>, XxxConsumer>();`
3. **Idempotent bắt buộc**: giao hàng ít nhất một lần, và khi một handler lỗi thì cả lô handler của event đó chạy lại. Dùng khóa tự nhiên + unique index (ví dụ `(SessionId, TeacherId)` của `Timesheet`), upsert, hoặc `context.EventId`. Lỗi unique khi chạy song song → throw để outbox thử lại (xem `SaveIgnoringDuplicateAsync` trong `HrHandlers.cs`).
4. Cần thêm dữ liệu → gọi `I<Other>Api`, không đọc schema module khác.

## Sửa Contracts

- `ArchitectureTests` giữ snapshot `tests/Konnyaku.ArchitectureTests/contracts.snapshot.txt`. Thêm event/DTO mới: chạy lại test với `KONNYAKU_UPDATE_SNAPSHOTS=1` rồi commit file snapshot.
- Đổi phá vỡ (bỏ/đổi kiểu field) **không sửa V1**: thêm `XxxV2`, phát cả hai trong giai đoạn chuyển tiếp, bỏ V1 khi không còn consumer.
- Event phải là `sealed record` tên kết thúc bằng `V<n>` (test kiểm tra).

## Test

Integration test: thực hiện hành động → `await _api.DrainOutboxAsync();` → kiểm tra trạng thái ở module nhận qua API. Có thể gọi `DrainOutboxAsync` hai lần để kiểm tra idempotent.
