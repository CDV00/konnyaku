# Chương 3 — Thiết kế Dữ liệu Lõi

## 3.0 Chọn PostgreSQL thay vì SQL Server — lý do kỹ thuật cụ thể

| Nhu cầu của Konnyaku | PostgreSQL | SQL Server |
|---|---|---|
| Chống chồng lịch GV/phòng/host **ở tầng DB** | `EXCLUDE USING gist (... WITH =, during WITH &&)` — một dòng, chắc chắn đúng kể cả khi 2 request đồng thời | Không có exclusion constraint → phải dùng `sp_getapplock`/`SERIALIZABLE` + kiểm tra thủ công, dễ sai |
| Kiểu khoảng thời gian | `tstzrange` native | Phải 2 cột + logic |
| Lấy job/host không tranh chấp | `FOR UPDATE SKIP LOCKED` | `READPAST` (có nhưng kém tiện) |
| JSON cấu hình policy, payload event | `jsonb` + index GIN | `nvarchar(max)` + JSON functions |
| Chi phí license khi scale | 0 | Đáng kể |

Nếu bắt buộc SQL Server: dùng **Temporal Tables** cho audit (điểm mạnh của SQL Server) và một bảng `ResourceSlot` (khóa theo ô 15 phút, unique `(ResourceId, SlotStart)`) để giả lập exclusion constraint.

## 3.1 Bản đồ module → schema

| Schema | Thực thể chính |
|---|---|
| `identity` | User, Role, Permission, ExternalLogin |
| `crm` | Lead, Student, Guardian, PlacementTest, Consent |
| `academic` | Program, Level, Curriculum, CurriculumLesson, Class, Enrollment |
| `scheduling` | SchedulePattern, Session, ResourceBooking, Room, SessionChangeLog |
| `workspace` | HostAccount, HostLease, MeetingSpace, ConferenceRecord, ParticipantSession, Recording, WorkspaceSubscription |
| `learning` | Attendance, SessionFeedback, Homework, Submission, SkillAssessment |
| `billing` | Package, Order, Invoice, Payment, CreditLedgerEntry, RefundRequest |
| `hr` | Teacher, Qualification, Availability, AvailabilityException, PayRate, TeacherTimesheet, PayrollPeriod, Payslip |
| `notify` | Template, Message, DeliveryAttempt |
| `platform` | OutboxMessage, InboxMessage, AuditLog, Setting |

## 3.2 ERD cốt lõi

```mermaid
erDiagram
  CLASS ||--o{ SCHEDULE_PATTERN : "lặp theo"
  CLASS ||--o{ SESSION : "gồm"
  SCHEDULE_PATTERN ||--o{ SESSION : "sinh ra"
  CURRICULUM_LESSON ||--o{ SESSION : "dạy bài"
  CLASS ||--o{ ENROLLMENT : "có"
  STUDENT ||--o{ ENROLLMENT : "ghi danh"
  ENROLLMENT ||--o{ CREDIT_LEDGER_ENTRY : "phát sinh"
  SESSION ||--o{ ATTENDANCE : "điểm danh"
  ENROLLMENT ||--o{ ATTENDANCE : "của"
  ATTENDANCE |o--o| CREDIT_LEDGER_ENTRY : "tiêu thụ 1 buổi"
  SESSION ||--o{ RESOURCE_BOOKING : "giữ"
  TEACHER ||--o{ RESOURCE_BOOKING : "bị giữ"
  ROOM ||--o{ RESOURCE_BOOKING : "bị giữ"
  SESSION ||--o| HOST_LEASE : "thuê"
  HOST_ACCOUNT ||--o{ HOST_LEASE : "cho thuê"
  SESSION ||--o| MEETING_SPACE : "phòng ảo"
  MEETING_SPACE ||--o{ CONFERENCE_RECORD : "diễn ra"
  CONFERENCE_RECORD ||--o{ PARTICIPANT_SESSION : "ai vào/ra"
  CONFERENCE_RECORD ||--o{ RECORDING : "ghi hình"
  SESSION ||--o{ TEACHER_TIMESHEET : "sinh công"
  TEACHER ||--o{ TEACHER_TIMESHEET : "của"
  PAYROLL_PERIOD ||--o{ TEACHER_TIMESHEET : "chốt trong"
```

## 3.3 Mô tả chi tiết các bảng phức tạp

### 3.3.1 `scheduling.schedule_pattern` — quy tắc lặp
| Cột | Kiểu | Ghi chú |
|---|---|---|
| id | uuid (v7) | UUIDv7 để sắp xếp theo thời gian, tốt cho index |
| class_id | uuid | |
| rrule | text | RFC 5545, vd `FREQ=WEEKLY;BYDAY=TU,TH;BYHOUR=19;BYMINUTE=0` |
| timezone | text | IANA, vd `Asia/Ho_Chi_Minh` — **giờ học neo theo múi giờ của lớp** |
| duration_minutes | int | |
| effective_from / effective_to | date | Đổi lịch giữa khóa = đóng pattern cũ, mở pattern mới |
| default_teacher_id, default_room_id | uuid null | |

### 3.3.2 `scheduling.session` — buổi học (thực thể trung tâm)
| Cột | Kiểu | Ghi chú |
|---|---|---|
| id | uuid | |
| class_id, pattern_id | uuid | pattern_id null nếu buổi phát sinh (bù/tăng cường) |
| lesson_id | uuid null | Liên kết `curriculum_lesson` — nền tảng cho Smart Make-up |
| sequence_no | int | Buổi thứ mấy của lớp |
| starts_at, ends_at | timestamptz | Lưu UTC |
| local_date | date | Ngày theo múi giờ lớp — để lọc/báo cáo không phải convert |
| delivery_mode | enum | `Online`, `Offline`, `Hybrid` |
| planned_teacher_id, actual_teacher_id | uuid | Phân biệt GV dự kiến / GV dạy thật (dạy thay) |
| room_id | uuid null | Phòng vật lý |
| status | enum | `Draft → Scheduled → Provisioned → InProgress → Completed` / `Cancelled` / `Rescheduled` |
| capacity, makeup_slots | int | |
| cancel_reason_code, cancelled_by | | Ảnh hưởng ledger (hủy do trung tâm → không trừ buổi) |
| row_version | xmin / bigint | Optimistic concurrency |

### 3.3.3 `scheduling.resource_booking` — chống xung đột hợp nhất
Một bảng duy nhất giữ mọi tài nguyên bị chiếm bởi Session: GV, phòng vật lý, host account.

```sql
CREATE EXTENSION IF NOT EXISTS btree_gist;

CREATE TABLE scheduling.resource_booking (
  id             uuid PRIMARY KEY,
  session_id     uuid NOT NULL,
  resource_type  text NOT NULL,        -- 'teacher' | 'room' | 'host_account'
  resource_id    uuid NOT NULL,
  during         tstzrange NOT NULL,   -- đã cộng buffer (di chuyển giữa cơ sở, chuẩn bị phòng)
  is_active      boolean NOT NULL DEFAULT true,
  CONSTRAINT ex_resource_no_overlap EXCLUDE USING gist (
    resource_type WITH =,
    resource_id   WITH =,
    during        WITH &&
  ) WHERE (is_active)
);
```
- Hai điều phối viên xếp cùng GV vào cùng giờ đồng thời → một transaction nhận lỗi `23P01 exclusion_violation` → API trả 409 kèm thông tin xung đột. **Không có race condition**, không cần lock thủ công.
- Học viên **không** đưa vào exclusion constraint (học viên có thể học 2 lớp chồng giờ do bù/ghép) → kiểm tra mềm, cảnh báo.
- `HostLease` thực chất là `resource_booking` với `resource_type='host_account'` + bảng chi tiết `workspace.host_lease` (trạng thái provisioning, space đã tạo).

### 3.3.4 `workspace.host_account`
| Cột | Ghi chú |
|---|---|
| email, sku (`BusinessStandard`…) | |
| status | `Active`, `Draining` (không nhận lease mới), `Suspended` |
| drive_quota_used_bytes, quota_checked_at | Cập nhật bởi job — chặn cấp phát khi > 85% |
| max_concurrent_leases | Mặc định 1 |
| tags | jsonb — vd `{"region":"vn","recording":true}` |

### 3.3.5 `academic.enrollment`
| Cột | Ghi chú |
|---|---|
| student_id, class_id, package_order_id | |
| type | `Regular`, `Trial`, `MakeupOnly` |
| status | `Pending → Active ⇄ Frozen → Completed` / `Transferred` / `Dropped` |
| starts_on, ends_on, frozen_from, frozen_to | |
| transferred_to_enrollment_id | Chuỗi chuyển lớp truy vết được |

### 3.3.6 `billing.credit_ledger_entry` — sổ cái buổi học (append-only)
| Cột | Ghi chú |
|---|---|
| id, enrollment_id, student_id | |
| entry_type | `Purchase`, `Bonus`, `Consume`, `Refund`, `TransferOut`, `TransferIn`, `Expire`, `Adjust` |
| credits | numeric(6,2), dương = cộng, âm = trừ (hỗ trợ buổi 0.5 nếu lớp 45') |
| unit_amount | numeric(18,2) — giá trị tiền / 1 credit, để tính doanh thu thực hiện & hoàn tiền |
| credit_kind | `Paid` / `Bonus` |
| attendance_id | Bắt buộc với `Consume` — **unique** để không trừ 2 lần một buổi |
| reason, created_by, created_at | |
- **Không UPDATE, không DELETE.** Sai → ghi bút toán `Adjust` đảo ngược. Số dư = `SUM(credits)`; cache vào `enrollment.credit_balance` cập nhật trong cùng transaction.
- Doanh thu thực hiện kỳ X = `SUM(-credits * unit_amount) WHERE entry_type='Consume' AND created_at ∈ X`.

### 3.3.7 `learning.attendance`
| Cột | Ghi chú |
|---|---|
| session_id, enrollment_id (unique cặp) | |
| status | `Present`, `Late`, `LeftEarly`, `Absent`, `ExcusedAbsent`, `Makeup` |
| source | `Auto` (từ Meet), `Manual`, `Override` |
| joined_at, left_at, present_seconds | Tính từ `participant_session` |
| confirmed_by, confirmed_at | GV xác nhận; sau khi chốt mới phát sinh `Consume` |
| billable | Do policy quyết định (vắng không phép có trừ buổi không?) |

### 3.3.8 `hr.teacher_timesheet`
| Cột | Ghi chú |
|---|---|
| teacher_id, session_id (unique cặp) | |
| role | `Lead`, `Assistant`, `Substitute` |
| planned_minutes, actual_minutes | actual từ participant log của GV |
| pay_rate_id, rate_snapshot | **Snapshot đơn giá tại thời điểm dạy** — đổi bảng lương sau không ảnh hưởng quá khứ |
| currency | VND/USD cho GV nước ngoài |
| adjustments | jsonb: phạt đi trễ, phụ cấp dạy thay gấp |
| status | `Draft → Approved → Locked` (Locked khi payroll period chốt) |
| payroll_period_id | |

## 3.4 Lưu ý thiết kế

### 3.4.1 Thời gian & múi giờ
1. Mọi thời điểm: `timestamptz`, ứng dụng dùng **NodaTime** (`Instant`, `ZonedDateTime`) thay vì `DateTime` — loại bỏ cả lớp bug `DateTimeKind`.
2. Lưu **IANA timezone** cùng dữ liệu "do con người định nghĩa": pattern lớp, availability GV, cơ sở vật lý. Không lưu offset (`+07:00`) — offset không biết DST.
3. Session được **materialize** từ pattern cho N tuần tới, tính bằng múi giờ của *lớp*. Availability GV được so khớp sau khi convert về UTC → DST của GV tự động phản ánh.
4. Job hằng tuần: tìm session tương lai nằm trong tuần chuyển DST của bất kỳ timezone nào của GV phụ trách → cảnh báo điều phối viên.
5. `local_date` lưu sẵn trên `session` để báo cáo theo ngày không bị lệch ngày ở 00:00 UTC (= 07:00 VN).

### 3.4.2 Lịch sử & Audit
- **Audit log tổng quát** (`platform.audit_log`): EF Core `SaveChangesInterceptor` ghi `entity, entity_id, action, changes(jsonb old/new), actor_id, correlation_id, at`. Partition theo tháng.
- **Lịch sử nghiệp vụ có ngữ nghĩa** (quan trọng hơn audit thô): `session_change_log` (đổi giờ/đổi GV + lý do + người yêu cầu), ledger append-only, enrollment status history. Báo cáo "GV X bị đổi lịch bao nhiêu lần do lỗi trung tâm" phải truy vấn được mà không phải parse JSON audit.
- Dữ liệu tài chính và timesheet: **khóa theo kỳ** (`Locked`), sửa sau khi khóa chỉ bằng bút toán điều chỉnh ở kỳ sau.

### 3.4.3 Toàn vẹn khi xếp lịch đồng thời
| Kỹ thuật | Ở đâu |
|---|---|
| Exclusion constraint | Chồng lịch GV / phòng / host — chân lý cuối cùng |
| Optimistic concurrency (`xmin` / `row_version`) | Sửa session, enrollment — hai người sửa cùng lúc → người sau phải tải lại |
| `SELECT … FOR UPDATE SKIP LOCKED` | Allocator chọn host, worker lấy job |
| Unique constraint nghiệp vụ | `(session_id, enrollment_id)` attendance; `attendance_id` trong ledger; `(class_id, sequence_no)` |
| Check sĩ số | `UPDATE session SET booked = booked + 1 WHERE id = $1 AND booked < capacity` — atomic, kiểm tra `rowcount` |
| Idempotency key | Bảng `platform.inbox_message (message_id PK)` cho webhook/Pub/Sub; header `Idempotency-Key` cho API thanh toán |

### 3.4.4 Khác
- **Soft delete chỉ cho dữ liệu danh mục** (khóa học, template). Dữ liệu giao dịch không xóa, chỉ đổi trạng thái.
- **Multi-tenant sẵn sàng**: thêm `tenant_id` vào mọi bảng từ ngày đầu + Row-Level Security PostgreSQL. Chi phí thêm gần bằng 0 bây giờ, nhưng là đường mở để bán Konnyaku dạng SaaS cho trung tâm khác (xem Chương 4, V2).
- **Dữ liệu cá nhân**: email/SĐT học viên, phụ huynh mã hóa cột hoặc tối thiểu tách bảng `crm.contact` có quyền truy cập riêng; consent lưu theo `(subject, purpose, version, granted_at, revoked_at)`.
- **Index gợi ý ban đầu**: `session (class_id, starts_at)`, `session (actual_teacher_id, starts_at)`, `session (status, starts_at)` cho job provisioning, `attendance (enrollment_id)`, `credit_ledger_entry (enrollment_id, created_at)`.
