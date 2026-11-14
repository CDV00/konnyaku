# Chương 1 — Khai phá & Mở rộng Bài toán Nghiệp vụ

> Nguyên tắc xuyên suốt: **Konnyaku không bán "phần mềm quản lý", mà bán "giờ dạy được tận dụng tối đa + học viên không rời đi"**. Mọi tính năng phải quy về một trong ba chỉ số: *tỷ lệ lấp đầy lớp*, *tỷ lệ giữ chân (retention)*, *chi phí vận hành trên mỗi giờ dạy*.

## 1.1 Phản biện 4 bài toán nền tảng

| Bài toán | Cách hiểu phổ biến (dễ sai) | Cách nên hiểu |
|---|---|---|
| Quản lý học viên | Hồ sơ + gói học + công nợ | **Sổ cái "buổi học" (session credit ledger)**: học viên mua *quyền học N buổi*, mỗi buổi tham dự/vắng/bù/bảo lưu là một bút toán. Công nợ, học phí chưa thực hiện, hoàn tiền đều suy ra từ sổ cái này. |
| Giảng viên & lương | Bảng chấm công nhập tay | **Timesheet được sinh ra từ dữ liệu thật** (Meet participant log: giờ vào/ra phòng của GV) → người chỉ *duyệt ngoại lệ*, không nhập. |
| Xếp lịch | Lịch tuần lặp lại | Hai lớp: **Pattern** (quy tắc lặp, múi giờ gốc) và **Session** (từng buổi đã "đông cứng"). Đổi lịch/bù/hủy chỉ đụng Session, không phá Pattern. |
| Google Workspace | Tạo link Meet | **Một "Virtual Room Engine"**: cấp phát tài nguyên (host account) như cấp phòng vật lý, có vòng đời, giám sát, và thu hồi artifact (record, transcript, attendance). |

## 1.2 Bảy tính năng mở rộng mang tính đột phá

### F1. Auto-Attendance từ Meet (không cần điểm danh tay)
- Google Meet REST API trả về `conferenceRecords.participants` + `participantSessions` (thời điểm vào/ra từng lần).
- Konnyaku ghép participant ↔ học viên qua email đăng nhập, tính **thời lượng có mặt thực tế**. Rule ví dụ: có mặt ≥ 70% thời lượng → *Present*; vào trễ > 10' → *Late*; < 30% → *Absent (joined briefly)*.
- GV chỉ xác nhận/sửa ngoại lệ trong 24h. Cùng dữ liệu đó sinh **timesheet GV** (F-Payroll) → một nguồn sự thật cho cả học phí lẫn lương.

### F2. AI Lesson Intelligence (từ recording + transcript)
- Meet sinh **transcript có gắn người nói** (transcript entries theo participant) → không cần tự làm diarization.
- Pipeline sau buổi học: tóm tắt nội dung, danh sách từ vựng/cấu trúc đã dạy, bài tập về nhà gợi ý, "điểm mấu chốt" gửi phụ huynh.
- **Chỉ số chất lượng giảng dạy**: tỷ lệ Teacher Talk Time / Student Talk Time (TTT/STT) — thước đo kinh điển trong ELT; lớp giao tiếp mà GV nói 80% là lớp có vấn đề. Cung cấp cho Academic Manager, không phải để phạt mà để coaching.

### F3. Speaking Score tự động & hồ sơ năng lực động
- Trích audio đoạn học viên nói (dựa trên timestamp transcript) → chấm pronunciation/fluency (Azure Speech Pronunciation Assessment hoặc model tự host) + LLM chấm grammar/vocab range theo thang CEFR.
- Kết quả cập nhật **Skill Profile** (Listening/Speaking/Reading/Writing × CEFR sub-level) sau mỗi buổi thay vì chỉ sau kỳ thi → lộ trình cá nhân hóa có dữ liệu thật.
- ⚠️ Chỉ bật khi có **đồng ý xử lý dữ liệu sinh trắc/giọng nói** (xem pain point P9).

### F4. Churn Early-Warning (cảnh báo sớm bỏ học)
- Bắt đầu bằng **rule-based score** (V2), chuyển sang model ML (V3) khi đủ dữ liệu. Tín hiệu:
  - Vắng 2 buổi liên tiếp / tỷ lệ tham dự 4 tuần giảm > 20%
  - Camera tắt / thời lượng có mặt giảm dần (từ participant sessions)
  - Không nộp homework, điểm speaking đi ngang
  - Còn ≤ 3 buổi trong gói mà chưa có hành vi gia hạn
  - Phụ huynh không mở tin nhắn Zalo ZNS
- Đầu ra: **task cho tư vấn viên** (gọi điện, đề xuất đổi lớp/đổi GV), không chỉ là dashboard.

### F5. Smart Make-up Matching (học bù thông minh)
- Học viên vắng buổi *Unit 5 – Lesson 2* → hệ thống tìm **các lớp khác cùng giáo trình, cùng level, sắp dạy đúng bài đó**, còn slot, khung giờ phù hợp múi giờ học viên → đề xuất 3 lựa chọn, học viên tự chọn trên portal.
- Điều kiện tiên quyết: giáo trình được mô hình hóa đến cấp *Lesson* và mỗi Session gắn `LessonId`. Đây là lý do thiết kế dữ liệu ở Chương 3 bắt buộc có `CurriculumLesson`.

### F6. Omnichannel Care Automation (Zalo ZNS / Email / Push)
- Event-driven: `SessionScheduled` → nhắc lịch T-24h & T-30'; `RecordingPublished` → gửi link xem lại; `CreditLow` → nhắc gia hạn; `ChurnRiskHigh` → kịch bản chăm sóc.
- Zalo ZNS cần template duyệt trước → Konnyaku quản lý **template registry** có version, fallback sang SMS/Email khi ZNS fail.

### F7. Gamification gắn với hành vi học thật
- XP/streak chỉ cộng từ **hành vi có giá trị**: tham dự đủ (từ F1), hoàn thành homework, cải thiện speaking score (F3). Tránh gamification "điểm danh cho có".
- Leaderboard theo lớp (không toàn trường — tránh làm nản học viên yếu), badge theo CEFR milestone, phụ huynh nhận "báo cáo tiến bộ" hàng tháng.

### (Bonus) F8. Adaptive Placement Test
- Test đầu vào thích ứng (CAT): câu hỏi khó dần/dễ dần theo câu trả lời, phần speaking 2' ghi âm chấm bằng pipeline F3 → tư vấn viên có kết quả trong 15', chốt sale ngay trong buổi tư vấn.

## 1.3 "Nỗi đau" vận hành thường bị bỏ sót

| # | Pain point | Vì sao đau | Hệ quả thiết kế |
|---|---|---|---|
| P1 | **Học bù (make-up)** | Bù vào lớp khác → lệch sĩ số, lệch bài, GV lớp kia không biết | Session có `Capacity` + `MakeupSlots` riêng; Enrollment tạm thời loại `MakeupAttendance`; credit ledger ghi nhận "đã dùng buổi bù" |
| P2 | **Bảo lưu (freeze)** ngang khóa | Học phí đã thu nhưng chưa dạy, hạn gói bị trễ, lớp gốc đã chạy tiếp | Enrollment có trạng thái `Frozen` với `FrozenFrom/To`, credit không hết hạn trong thời gian freeze; quy định số lần/tối đa ngày bảo lưu là **policy cấu hình**, không hard-code |
| P3 | **Chuyển lớp / chuyển level giữa chừng** | Tính lại học phí chênh lệch, chuyển credit | Transfer = đóng Enrollment cũ + mở Enrollment mới, credit chuyển qua bút toán `Transfer` (không sửa số dư trực tiếp) |
| P4 | **Đổi GV đột xuất (substitute)** | GV ốm 30' trước giờ học; lương ai hưởng; học viên phàn nàn | Session có `PlannedTeacherId` vs `ActualTeacherId`; công cụ "tìm GV thay thế" theo availability + skill tag + múi giờ; timesheet trả cho *actual* |
| P5 | **Lệch múi giờ & DST với GV nước ngoài** | GV ở UK/US đổi giờ mùa hè → lệch 1 tiếng với học viên VN (VN không có DST); GV Philippines không DST | Lưu UTC + **IANA timezone** của *chủ thể sở hữu lịch* (lớp neo theo `Asia/Ho_Chi_Minh`); availability GV lưu theo timezone của GV; cảnh báo trước 2 tuần mỗi kỳ chuyển DST |
| P6 | **Recording lỗi / không có** | GV quên bấm record, Meet lỗi, học viên khiếu nại | Bật auto-recording ở cấu hình space; job đối soát "Session đã diễn ra nhưng không có recording sau 3h" → alert |
| P7 | **Host account hết dung lượng Drive** | Business Standard 2 TB/user (pooled) — recording 1h ≈ vài trăm MB–1 GB; pool account đầy sau vài tháng | Job chuyển recording sang **Shared Drive** hoặc object storage riêng ngay khi `fileGenerated`; dashboard dung lượng pool |
| P8 | **Học viên không có Google account / dùng email khác** | Không phân quyền Drive được, không khớp được điểm danh | Bắt buộc **"Konnyaku identity email"** khi ghi danh; fallback: stream recording qua proxy của Konnyaku thay vì share Drive |
| P9 | **Pháp lý dữ liệu cá nhân & trẻ vị thành niên** | Ghi hình trẻ em, dữ liệu giọng nói là dữ liệu nhạy cảm theo khung pháp luật bảo vệ DLCN tại VN | Consent record có version, theo từng mục đích (ghi hình / AI chấm điểm / marketing); với học viên < 16 tuổi cần đồng ý của phụ huynh; retention policy tự xóa recording sau N tháng |
| P10 | **Doanh thu chưa thực hiện** | Kế toán phải ghi nhận doanh thu theo buổi đã dạy, không theo tiền thu | Credit ledger cho phép xuất báo cáo *deferred revenue* theo kỳ một cách chính xác |
| P11 | **Hoàn tiền & khuyến mãi phức tạp** | Gói 48 buổi tặng 4 buổi, hoàn tiền thì tính buổi tặng thế nào | Credit có `UnitPrice` thực tế (tiền thu / số buổi tính phí) và loại credit `Paid` vs `Bonus`; policy hoàn tiền tiêu thụ `Bonus` trước hay sau là cấu hình |
| P12 | **Rủi ro chia sẻ tài khoản pool** | Đưa password host account cho GV = mất kiểm soát, vi phạm bảo mật | GV **không bao giờ đăng nhập** host account; GV tham gia bằng tài khoản riêng và được nâng quyền co-host / host account chỉ là "chủ sở hữu phòng" do hệ thống vận hành qua domain-wide delegation |

## 1.4 Lưu ý chiến lược về Host Account Pooling

Đây là ý tưởng tiết kiệm chi phí hấp dẫn nhưng cần nhìn thẳng rủi ro:

1. **Điều khoản cấp phép**: license Google Workspace cấp *theo người dùng*. Dùng account như "phòng học" do hệ thống vận hành (không giao cho người cụ thể) là vùng xám — **cần xác nhận với Google reseller** trước khi scale. Thiết kế Konnyaku phải để **Room Provider là abstraction** (`IVirtualRoomProvider`) để có thể chuyển sang mô hình "mỗi GV một license" hoặc Zoom/Teams mà không đập lại hệ thống.
2. **Bài toán kinh tế**: số license cần = **số buổi học đồng thời tối đa (peak concurrency)**, không phải số GV. Trung tâm 40 GV nhưng peak 12 lớp cùng lúc lúc 19h → 12–14 license (thêm buffer). Konnyaku nên có **báo cáo peak concurrency** để chủ trung tâm tự thấy số tiền tiết kiệm.
3. **Peak giờ vàng** (18h–21h ngày thường): chính khung giờ này quyết định số license. Tính năng "gợi ý dời lớp sang giờ thấp điểm kèm ưu đãi học phí" giúp giảm license — một ví dụ AI/automation tạo ra tiền thật.
