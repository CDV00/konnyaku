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
| `platform` | Tenant, TenantDomain, TenantPlacement, Plan, Subscription, FeatureFlag, Setting (kế thừa theo scope) |
| `catalog` *(dữ liệu chuẩn của nền tảng)* | Language (BCP 47), ProficiencyFramework, ProficiencyLevel, SkillDimension, Country, PublicHoliday |
| `identity` | User (**toàn nền tảng**), ExternalLogin (Google/Zalo), LocalCredential (tài khoản trung tâm cấp, theo tenant), Membership (user ↔ tenant), Role, Permission, RoleAssignment (theo scope tenant/nhóm cơ sở/cơ sở) |
| `org` | Branch, BranchGroup, Room, BranchTravelTime, HolidayCalendar |
| `crm` | Lead, StudentProfile, Guardianship, PlacementTest, Consent |
| `academic` | Program (ngôn ngữ + khung năng lực), CourseLevel, Curriculum, CurriculumLesson, Class, Enrollment, SessionSeat (sĩ số & chỗ học bù theo buổi) |
| `scheduling` | SchedulePattern, Session, ResourceBooking, SessionChangeLog |
| `workspace` | WorkspaceConnection (của tenant), HostAccount, HostLease (có exclusion constraint riêng), MeetingSpace, ConferenceRecord, ParticipantSession, Recording, WorkspaceSubscription |
| `learning` | Attendance, SessionFeedback, Homework, Submission, StudentSkillScore |
| `billing` | PriceList, Package, Order, Invoice, Payment, CreditLedgerEntry, CreditBalance, RefundRequest |
| `hr` | TeacherProfile, TeacherCapability, Certification, Availability, AvailabilityException, PayRate, TeacherTimesheet, PayrollPeriod, Payslip |
| `notify` | Template (theo kênh + locale), Message, DeliveryAttempt |

Quy ước chung: **mọi bảng nghiệp vụ có `tenant_id NOT NULL`** và bật Row-Level Security; chỉ `catalog.*`, `identity.user` và một phần `platform.*` là dữ liệu toàn nền tảng. Bảng liên quan cơ sở có `branch_id` (snapshot tại thời điểm phát sinh với bảng giao dịch). Mỗi module có bảng `outbox_message`, `inbox_message`, `audit_log` và `sequence` (bộ đếm mã hiển thị) **trong schema của mình** — không module nào ghi vào schema của module khác. Danh sách module chuẩn: [6.2](06-evolution-strategy.md#62-bản-đồ-module-bounded-context--nguồn-chuẩn-duy-nhất); quy tắc đa tenant: [7.4](07-multi-tenant-multi-language.md#74-cô-lập-dữ-liệu-giữa-các-tenant).

Quy ước tên: `student_id` / `teacher_id` trong các bảng dưới nghĩa là `crm.student_profile.id` / `hr.teacher_profile.id` (hồ sơ **theo tenant**), không phải `identity.user.id`.

## 3.2 ERD cốt lõi

```mermaid
erDiagram
  TENANT ||--o{ BRANCH : "có"
  BRANCH ||--o{ ROOM : "có"
  BRANCH ||--o{ CLASS : "quản lý"
  PROGRAM ||--o{ CLASS : "mở lớp"
  LANGUAGE ||--o{ PROGRAM : "dạy"
  CLASS ||--o{ SCHEDULE_PATTERN : "lặp theo"
  CLASS ||--o{ SESSION : "gồm"
  SCHEDULE_PATTERN ||--o{ SESSION : "sinh ra"
  CURRICULUM_LESSON ||--o{ SESSION : "dạy bài"
  CLASS ||--o{ ENROLLMENT : "có"
  STUDENT_PROFILE ||--o{ ENROLLMENT : "ghi danh"
  ENROLLMENT ||--o{ CREDIT_LEDGER_ENTRY : "phát sinh"
  ENROLLMENT ||--|| CREDIT_BALANCE : "số dư"
  SESSION ||--o{ ATTENDANCE : "điểm danh"
  ENROLLMENT ||--o{ ATTENDANCE : "của"
  ATTENDANCE |o--o| CREDIT_LEDGER_ENTRY : "tiêu thụ 1 buổi"
  SESSION ||--o{ RESOURCE_BOOKING : "giữ GV/phòng"
  TEACHER_PROFILE ||--o{ RESOURCE_BOOKING : "bị giữ"
  ROOM ||--o{ RESOURCE_BOOKING : "bị giữ"
  SESSION ||--o| HOST_LEASE : "thuê host"
  HOST_ACCOUNT ||--o{ HOST_LEASE : "cho thuê"
  SESSION ||--o| MEETING_SPACE : "phòng ảo"
  MEETING_SPACE ||--o{ CONFERENCE_RECORD : "diễn ra"
  CONFERENCE_RECORD ||--o{ PARTICIPANT_SESSION : "ai vào/ra"
  CONFERENCE_RECORD ||--o{ RECORDING : "ghi hình"
  SESSION ||--o{ TEACHER_TIMESHEET : "sinh công"
  TEACHER_PROFILE ||--o{ TEACHER_TIMESHEET : "của"
  PAYROLL_PERIOD ||--o{ TEACHER_TIMESHEET : "chốt trong"
```

Đường nối giữa hai module khác nhau trong ERD là **tham chiếu bằng ID**, không phải khóa ngoại (xem [6.3](06-evolution-strategy.md#63-mười-đường-cắt-bắt-buộc)).

## 3.3 Mô tả chi tiết các bảng phức tạp

Mọi bảng dưới đây **ngầm có** `id uuid (v7)`, `tenant_id uuid NOT NULL` (RLS) và `created_at/created_by`; chỉ ghi ra khi có ý nghĩa riêng.

### 3.3.1 `scheduling.schedule_pattern` — quy tắc lặp
| Cột | Kiểu | Ghi chú |
|---|---|---|
| class_id | uuid | |
| rrule | text | RFC 5545, vd `FREQ=WEEKLY;BYDAY=TU,TH;BYHOUR=19;BYMINUTE=0` |
| timezone | text | IANA — mặc định lấy từ cơ sở quản lý lớp; **giờ học neo theo múi giờ của lớp** |
| duration_minutes | int | |
| effective_from / effective_to | date | Đổi lịch giữa khóa = đóng pattern cũ, mở pattern mới |
| default_teacher_id, default_room_id | uuid null | |

### 3.3.2 `scheduling.session` — buổi học (thực thể trung tâm)
| Cột | Kiểu | Ghi chú |
|---|---|---|
| branch_id | uuid | Cơ sở quản lý lớp tại thời điểm buổi học — doanh thu/chi phí GV quy về đây |
| class_id, pattern_id | uuid | pattern_id null nếu buổi phát sinh (bù/tăng cường) |
| lesson_id | uuid null | Liên kết `curriculum_lesson` — nền tảng cho Smart Make-up |
| sequence_no | int | Buổi thứ mấy của lớp |
| starts_at, ends_at | timestamptz | Lưu UTC |
| local_date | date | Ngày theo múi giờ lớp — để lọc/báo cáo không phải convert |
| delivery_mode | enum | `Online`, `Offline`, `Hybrid` |
| planned_teacher_id, actual_teacher_id | uuid | Phân biệt GV dự kiến / GV dạy thật (dạy thay) |
| room_id | uuid null | Phòng vật lý |
| status | enum | `Draft → Scheduled → Provisioned → InProgress → Completed` / `Cancelled` / `Rescheduled` |
| cancel_reason_code, cancelled_by | | Ảnh hưởng ledger (hủy do trung tâm → không trừ buổi) |
| row_version | xmin / bigint | Optimistic concurrency |

Sĩ số và chỗ học bù **không** nằm ở đây mà ở `academic.session_seat (session_id, capacity, booked, makeup_capacity, makeup_booked)` — vì giữ chỗ là việc của Academic; bảng này được Academic tạo từ `SessionScheduledV1`.

### 3.3.3 `scheduling.resource_booking` — chống xung đột GV & phòng
```sql
CREATE EXTENSION IF NOT EXISTS btree_gist;

CREATE TABLE scheduling.resource_booking (
  id             uuid PRIMARY KEY,
  tenant_id      uuid NOT NULL,        -- RLS; resource_id là UUID toàn cục nên không cần đưa vào constraint
  session_id     uuid NOT NULL,
  resource_type  text NOT NULL,        -- 'teacher' | 'room'
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
- Host account **không** nằm ở đây — nó thuộc module `Workspace` (3.3.4), để khi tách module, schema `workspace` mang theo đủ ràng buộc của mình.

### 3.3.4 `workspace.host_account` & `workspace.host_lease`
| `host_account` | Ghi chú |
|---|---|
| workspace_connection_id | Kết nối Google Workspace của tenant sở hữu account |
| email, sku | Gói có tính năng ghi hình |
| status | `Active`, `Draining` (không nhận lease mới), `Suspended` |
| max_concurrent_leases | Mặc định 1 |
| tags | jsonb — vd `{"region":"vn"}` |

```sql
CREATE TABLE workspace.host_lease (
  id               uuid PRIMARY KEY,
  tenant_id        uuid NOT NULL,
  host_account_id  uuid NOT NULL,
  session_id       uuid NOT NULL,
  during           tstzrange NOT NULL,   -- [start − 15', end + 15')
  status           text NOT NULL,        -- 'Reserved' | 'Provisioned' | 'Released' | 'Cancelled'
  space_name       text NULL,
  CONSTRAINT ex_host_no_overlap EXCLUDE USING gist (
    host_account_id WITH =,
    during          WITH &&
  ) WHERE (status IN ('Reserved', 'Provisioned'))
);
```
Dung lượng: Google Workspace tính dung lượng **gộp theo tổ chức**, không theo từng host → theo dõi dung lượng ở cấp `workspace_connection` và áp chính sách lưu giữ (xóa/chuyển recording cũ), không chặn cấp phát theo từng host.

### 3.3.5 `academic.enrollment`
| Cột | Ghi chú |
|---|---|
| student_id, class_id, order_id | `order_id` tham chiếu `billing.order` |
| type | `Regular`, `Trial`, `MakeupOnly` |
| status | `Pending → Active ⇄ Frozen → Completed` / `Rejected` (lớp đủ sĩ số khi thanh toán xong) / `Transferred` / `Dropped` |
| starts_on, ends_on, frozen_from, frozen_to | |
| transferred_to_enrollment_id | Chuỗi chuyển lớp truy vết được |

### 3.3.6 `billing.credit_ledger_entry` — sổ cái buổi học (append-only)
| Cột | Ghi chú |
|---|---|
| enrollment_id, student_id | |
| branch_id | Với `Consume`: cơ sở của lớp tại buổi học; với `Purchase`: cơ sở thu tiền — nền tảng báo cáo doanh thu theo cơ sở |
| entry_type | `Purchase`, `Bonus`, `Consume`, `Refund`, `TransferOut`, `TransferIn`, `Expire`, `Adjust` |
| credits | numeric(6,2), dương = cộng, âm = trừ (hỗ trợ buổi 0.5 nếu lớp 45') |
| unit_amount | numeric(18,2) — giá trị tiền / 1 credit (credit `Bonus` = 0) |
| currency | ISO 4217 của tenant/cơ sở |
| credit_kind | `Paid` / `Bonus` |
| attendance_id | Bắt buộc với `Consume` — **unique** để không trừ 2 lần một buổi |
| session_local_date | Với `Consume`: `local_date` của buổi học — kỳ ghi nhận doanh thu |
| reason | |
- **Không UPDATE, không DELETE.** Sai → ghi bút toán `Adjust` đảo ngược.
- Số dư lưu ở `billing.credit_balance (enrollment_id, paid_credits, bonus_credits, row_version)`, cập nhật **trong cùng transaction với ledger** (cùng schema `billing`). Module khác biết số dư qua `IBillingApi` hoặc event `CreditBalanceChangedV1`.
- Doanh thu thực hiện kỳ X theo cơ sở = `SUM(-credits * unit_amount) WHERE entry_type='Consume' AND session_local_date ∈ X GROUP BY branch_id`. Buổi dùng credit `Bonus` không tạo doanh thu.

### 3.3.7 `learning.attendance`
| Cột | Ghi chú |
|---|---|
| session_id, enrollment_id (unique cặp) | |
| status | `Present`, `Late`, `LeftEarly`, `Absent`, `ExcusedAbsent`, `Makeup` |
| source | `Auto` (từ Meet), `Manual`, `Override` |
| joined_at, left_at, present_seconds | Tính từ `ParticipantsCollectedV1` |
| confirmed_by, confirmed_at | GV xác nhận; sau khi chốt mới phát `AttendanceConfirmedV1` → Billing ghi `Consume` |
| billable | Do policy quyết định (vắng không phép có trừ buổi không?) |

### 3.3.8 `hr.teacher_timesheet`
| Cột | Ghi chú |
|---|---|
| teacher_id, session_id (unique cặp) | |
| branch_id | Cơ sở chịu chi phí |
| role | `Lead`, `Assistant`, `Substitute` |
| planned_minutes, actual_minutes | actual từ `ParticipantsCollectedV1` |
| pay_rate_id, rate_snapshot | **Snapshot đơn giá tại thời điểm dạy** — đổi bảng lương sau không ảnh hưởng quá khứ |
| currency | ISO 4217 theo hợp đồng GV (vd USD cho GV nước ngoài) |
| adjustments | jsonb: phạt đi trễ, phụ cấp dạy thay gấp |
| status | `Draft → Approved → Locked` (Locked khi payroll period chốt) |
| payroll_period_id | |

## 3.4 Lưu ý thiết kế

### 3.4.1 Thời gian & múi giờ
1. Mọi thời điểm: `timestamptz`, ứng dụng dùng **NodaTime** (`Instant`, `ZonedDateTime`) thay vì `DateTime` — loại bỏ cả lớp bug `DateTimeKind`.
2. Lưu **IANA timezone** cùng dữ liệu "do con người định nghĩa": pattern lớp, availability GV, cơ sở vật lý. Không lưu offset (`+07:00`) — offset không biết DST.
3. Múi giờ của lớp mặc định lấy từ cơ sở quản lý lớp (cơ sở có thể ở nước khác). Session được **materialize** từ pattern cho N tuần tới, tính bằng múi giờ của *lớp*, **bỏ qua ngày trong lịch nghỉ** của cơ sở/tenant (Tết âm lịch là dữ liệu nhập theo năm). Availability GV được so khớp sau khi convert về UTC → DST của GV tự động phản ánh.
4. Job hằng tuần: tìm session tương lai nằm trong tuần chuyển DST của bất kỳ timezone nào của GV phụ trách → cảnh báo điều phối viên.
5. `local_date` lưu sẵn trên `session` để báo cáo theo ngày không bị lệch ngày ở 00:00 UTC (= 07:00 VN).

### 3.4.2 Lịch sử & Audit
- **Audit log tổng quát** (`<module>.audit_log`, mỗi module một bảng): EF Core `SaveChangesInterceptor` ghi `entity, entity_id, action, changes(jsonb old/new), actor_id, correlation_id, at`. Partition theo tháng.
- **Lịch sử nghiệp vụ có ngữ nghĩa** (quan trọng hơn audit thô): `session_change_log` (đổi giờ/đổi GV + lý do + người yêu cầu), ledger append-only, enrollment status history. Báo cáo "GV X bị đổi lịch bao nhiêu lần do lỗi trung tâm" phải truy vấn được mà không phải parse JSON audit.
- Dữ liệu tài chính và timesheet: **khóa theo kỳ** (`Locked`), sửa sau khi khóa chỉ bằng bút toán điều chỉnh ở kỳ sau.

### 3.4.3 Toàn vẹn khi xếp lịch đồng thời
| Kỹ thuật | Ở đâu |
|---|---|
| Exclusion constraint | Chồng lịch GV / phòng / host — chân lý cuối cùng |
| Optimistic concurrency (`xmin` / `row_version`) | Sửa session, enrollment — hai người sửa cùng lúc → người sau phải tải lại |
| `SELECT … FOR UPDATE SKIP LOCKED` | Allocator chọn host, worker lấy job |
| Unique constraint nghiệp vụ | `(session_id, enrollment_id)` attendance; `attendance_id` trong ledger; `(class_id, sequence_no)` |
| Check sĩ số | `UPDATE academic.session_seat SET booked = booked + 1 WHERE session_id = $1 AND booked < capacity` — atomic, kiểm tra `rowcount` |
| Idempotency key | Bảng `<module>.inbox_message (message_id PK)` cho webhook/Pub/Sub; header `Idempotency-Key` cho API thanh toán |

### 3.4.4 Khác
- **Soft delete chỉ cho dữ liệu danh mục** (khóa học, template). Dữ liệu giao dịch không xóa, chỉ đổi trạng thái.
- **Multi-tenant**: unique constraint trên **mã nghiệp vụ** luôn gồm `tenant_id` (mã học viên, mã lớp duy nhất *trong* tenant); khóa UUID toàn cục thì không cần. Toàn bộ quy tắc RLS: [7.4](07-multi-tenant-multi-language.md#74-cô-lập-dữ-liệu-giữa-các-tenant).
- **Dữ liệu cá nhân**: email/SĐT học viên, phụ huynh mã hóa cột hoặc tối thiểu tách bảng `crm.contact` có quyền truy cập riêng; consent lưu theo `(subject, purpose, version, granted_at, revoked_at)`.
- **Index gợi ý ban đầu**: `session (tenant_id, branch_id, starts_at)`, `session (class_id, starts_at)`, `session (actual_teacher_id, starts_at)`, `session (status, starts_at)` cho job provisioning, `attendance (enrollment_id)`, `credit_ledger_entry (enrollment_id, created_at)`. Với bảng lớn, `tenant_id` đứng đầu index để RLS không phải quét chéo tenant.

### 3.4.5 Dữ liệu đa ngôn ngữ
- Mã ngôn ngữ dùng **BCP 47** (`en`, `ja`, `zh-Hans`) ở mọi nơi: ngôn ngữ giảng dạy, locale giao diện, locale liên lạc.
- Trường hiển thị cho người ngoài dùng `LocalizedString` (`jsonb`); level theo bảng khung năng lực, không hard-code; collation & tìm kiếm CJK: xem [7.6](07-multi-tenant-multi-language.md#76-đa-ngôn-ngữ-giảng-dạy) và [7.7](07-multi-tenant-multi-language.md#77-giao-diện-đa-ngôn-ngữ-i18n).
