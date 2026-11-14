# Chương 1 — Khai phá & Mở rộng Bài toán Nghiệp vụ

> Nguyên tắc xuyên suốt: **Konnyaku không bán "phần mềm quản lý", mà bán "giờ dạy được tận dụng tối đa + học viên không rời đi"**. Mọi tính năng phải quy về một trong ba chỉ số: *tỷ lệ lấp đầy lớp*, *tỷ lệ giữ chân (retention)*, *chi phí vận hành trên mỗi giờ dạy*.
>
> Phạm vi: **nền tảng SaaS cho trung tâm ngoại ngữ** — nhiều trung tâm (tenant), mỗi trung tâm nhiều cơ sở, dạy nhiều ngôn ngữ (Anh, Nhật, Trung, Hàn, Pháp, Đức, tiếng Việt cho người nước ngoài…), giao diện đa ngôn ngữ do người dùng chọn. Chi tiết thiết kế các trục này ở [Chương 7](07-multi-tenant-multi-language.md); tiếng Anh chỉ là ví dụ chính trong các chương còn lại.

## 1.1 Phản biện 4 bài toán nền tảng

| Bài toán | Cách hiểu phổ biến (dễ sai) | Cách nên hiểu |
|---|---|---|
| Quản lý học viên | Hồ sơ + gói học + công nợ | **Sổ cái "buổi học" (session credit ledger)**: học viên mua *quyền học N buổi*, mỗi buổi tham dự/vắng/bù/bảo lưu là một bút toán. Công nợ, học phí chưa thực hiện, hoàn tiền đều suy ra từ sổ cái này. |
| Giáo viên & lương | Bảng chấm công nhập tay | **Timesheet được sinh ra từ dữ liệu thật** (Meet participant log: giờ vào/ra phòng của GV) → người chỉ *duyệt ngoại lệ*, không nhập. |
| Xếp lịch | Lịch tuần lặp lại | Hai lớp: **Pattern** (quy tắc lặp, múi giờ gốc) và **Session** (từng buổi đã "đông cứng"). Đổi lịch/bù/hủy chỉ đụng Session, không phá Pattern. |
| Google Workspace | Tạo link Meet | **Một Meeting Engine** (module `Workspace`): cấp phát tài nguyên (host account) như cấp phòng vật lý, có vòng đời, giám sát, và thu hồi artifact (record, transcript, attendance). |

## 1.2 Tám tính năng mở rộng mang tính đột phá

### F1. Auto-Attendance từ Meet (không cần điểm danh tay)
- Google Meet REST API trả về `conferenceRecords.participants` + `participantSessions` (thời điểm vào/ra từng lần).
- Konnyaku ghép participant ↔ học viên theo **Google user ID** (ghi lại lần đầu học viên vào lớp qua link Konnyaku khi đã đăng nhập Google — Meet API không đảm bảo trả email), tính **thời lượng có mặt thực tế**. Rule ví dụ: có mặt ≥ 70% thời lượng → `Present`; vào trễ > 10' → `Late`; rời sớm → `LeftEarly`; < 30% → `Absent`.
- Học viên dùng tài khoản do trung tâm cấp (không Google) vào lớp với tư cách khách → GV xác nhận tay.
- GV chỉ xác nhận/sửa ngoại lệ trong 24h. Cùng dữ liệu đó sinh **timesheet GV** (module `Hr`) → một nguồn sự thật cho cả học phí lẫn lương.

### F2. AI Lesson Intelligence (từ recording + transcript)
- Meet sinh **transcript có gắn người nói** (transcript entries theo participant) → không cần tự làm diarization (học viên vào với tư cách khách chỉ có tên hiển thị).
- Pipeline sau buổi học: tóm tắt nội dung, danh sách từ vựng/cấu trúc đã dạy, bài tập về nhà gợi ý, "điểm mấu chốt" gửi phụ huynh.
- **Chỉ số chất lượng giảng dạy**: tỷ lệ Teacher Talk Time / Student Talk Time (TTT/STT) — thước đo kinh điển trong ELT; lớp giao tiếp mà GV nói 80% là lớp có vấn đề. Cung cấp cho Academic Manager, không phải để phạt mà để coaching.

### F3. Speaking Score tự động & hồ sơ năng lực động
- Trích audio đoạn học viên nói (dựa trên timestamp transcript) → chấm pronunciation/fluency (Azure Speech Pronunciation Assessment hoặc model tự host) + LLM chấm grammar/vocab range theo **khung năng lực của ngôn ngữ đó** (CEFR, JLPT, HSK, TOPIK…).
- Kết quả cập nhật **Skill Profile** (kỹ năng của khung × level) sau mỗi buổi thay vì chỉ sau kỳ thi → lộ trình cá nhân hóa có dữ liệu thật.
- Mức hỗ trợ khác nhau theo ngôn ngữ (thanh điệu tiếng Trung, cao độ tiếng Nhật) → bật/tắt theo ma trận khả năng AI ([7.6.3](07-multi-tenant-multi-language.md#763-ai-theo-từng-ngôn-ngữ)).
- ⚠️ Chỉ bật khi có **đồng ý xử lý dữ liệu sinh trắc/giọng nói** (xem pain point P9).

### F4. Churn Early-Warning (cảnh báo sớm bỏ học)
- Bắt đầu bằng **rule-based score** (V2), chuyển sang model ML (V3) khi đủ dữ liệu. Tín hiệu:
  - Vắng 2 buổi liên tiếp / tỷ lệ tham dự 4 tuần giảm > 20%
  - Thời lượng có mặt giảm dần (từ participant sessions; trạng thái camera không có trong Meet REST API)
  - Không nộp homework, điểm speaking đi ngang
  - Còn ≤ 3 buổi trong gói mà chưa có hành vi gia hạn
  - Phụ huynh không mở tin nhắn Zalo ZNS
- Đầu ra: **task cho tư vấn viên** (gọi điện, đề xuất đổi lớp/đổi GV), không chỉ là dashboard.

### F5. Smart Make-up Matching (học bù thông minh)
- Học viên vắng buổi *Unit 5 – Lesson 2* → hệ thống tìm **các lớp khác cùng giáo trình, cùng level, sắp dạy đúng bài đó**, còn slot, khung giờ phù hợp múi giờ học viên → đề xuất 3 lựa chọn, học viên tự chọn trên portal.
- Điều kiện tiên quyết: giáo trình được mô hình hóa đến cấp *Lesson* và mỗi Session gắn `LessonId`. Đây là lý do thiết kế dữ liệu ở Chương 3 bắt buộc có `CurriculumLesson`.

### F6. Omnichannel Care Automation (Zalo ZNS / Email / Push)
- Event-driven ([6.2.1](06-evolution-strategy.md#621-danh-mục-integration-event-chính)): `MeetingProvisionedV1` → nhắc lịch T-24h & T-30'; `RecordingPublishedV1` → gửi link xem lại; `CreditLowV1` → nhắc gia hạn; điểm churn cao → kịch bản chăm sóc.
- Zalo ZNS cần template duyệt trước → Konnyaku quản lý **template registry** có version, fallback sang SMS/Email khi ZNS fail.

### F7. Gamification gắn với hành vi học thật
- XP/streak chỉ cộng từ **hành vi có giá trị**: tham dự đủ (từ F1), hoàn thành homework, cải thiện speaking score (F3). Tránh gamification "điểm danh cho có".
- Leaderboard theo lớp (không toàn trường — tránh làm nản học viên yếu), badge theo mốc level của khung năng lực (CEFR, JLPT, HSK…), phụ huynh nhận "báo cáo tiến bộ" hàng tháng.

### (Bonus) F8. Adaptive Placement Test
- Test đầu vào thích ứng (CAT), ngân hàng câu hỏi riêng theo từng ngôn ngữ + khung năng lực: câu hỏi khó dần/dễ dần theo câu trả lời, phần speaking 2' ghi âm chấm bằng pipeline F3 → tư vấn viên có kết quả trong 15', chốt sale ngay trong buổi tư vấn.

## 1.3 "Nỗi đau" vận hành thường bị bỏ sót

| # | Pain point | Vì sao đau | Hệ quả thiết kế |
|---|---|---|---|
| P1 | **Học bù (make-up)** | Bù vào lớp khác → lệch sĩ số, lệch bài, GV lớp kia không biết | Mỗi buổi có sĩ số + chỗ học bù riêng (`academic.session_seat`); enrollment loại `MakeupOnly`; attendance `Makeup` trừ credit của enrollment gốc |
| P2 | **Bảo lưu (freeze)** ngang khóa | Học phí đã thu nhưng chưa dạy, hạn gói bị trễ, lớp gốc đã chạy tiếp | Enrollment có trạng thái `Frozen` với `FrozenFrom/To`, credit không hết hạn trong thời gian freeze; quy định số lần/tối đa ngày bảo lưu là **policy cấu hình**, không hard-code |
| P3 | **Chuyển lớp / chuyển level giữa chừng** | Tính lại học phí chênh lệch, chuyển credit | Transfer = đóng Enrollment cũ + mở Enrollment mới, credit chuyển qua bút toán `TransferOut`/`TransferIn` (không sửa số dư trực tiếp) |
| P4 | **Đổi GV đột xuất (substitute)** | GV ốm 30' trước giờ học; lương ai hưởng; học viên phàn nàn | Session có `PlannedTeacherId` vs `ActualTeacherId`; công cụ "tìm GV thay thế" theo availability + skill tag + múi giờ; timesheet trả cho *actual* |
| P5 | **Lệch múi giờ & DST với GV nước ngoài** | GV ở UK/US đổi giờ mùa hè → lệch 1 tiếng với học viên VN (VN không có DST); GV Philippines không DST | Lưu UTC + **IANA timezone** của *chủ thể sở hữu lịch* (lớp neo theo múi giờ của cơ sở quản lý, vd `Asia/Ho_Chi_Minh`); availability GV lưu theo timezone của GV; cảnh báo trước 2 tuần mỗi kỳ chuyển DST |
| P6 | **Recording lỗi / không có** | GV quên bấm record, Meet lỗi, học viên khiếu nại | Bật ghi hình tự động ở cấu hình space (cần xác minh API trong spike, [2.3.4](02-architecture.md#234-điểm-then-chốt-trong-luồng)); job đối soát "Session đã diễn ra nhưng không có recording sau 3h" → alert |
| P7 | **Dung lượng lưu trữ recording** | Dung lượng Google Workspace tính **gộp cho cả tổ chức** — recording 1h ≈ vài trăm MB–1 GB, cả trung tâm đầy sau vài tháng | Gom recording vào **Shared Drive** của trung tâm, theo dõi dung lượng gộp, chính sách lưu giữ tự xóa/chuyển sang object storage rẻ hơn |
| P8 | **Học viên không có Google account** (trẻ nhỏ dùng tài khoản trung tâm cấp) | Không phân quyền Drive được, không ghép được điểm danh tự động, vào Meet phải xin vào | Phòng cho phép khách xin vào + GV là co-host duyệt; điểm danh GV xác nhận tay; xem lại video qua player HLS của Konnyaku ([2.3.4](02-architecture.md#234-điểm-then-chốt-trong-luồng)) |
| P9 | **Pháp lý dữ liệu cá nhân & trẻ vị thành niên** | Ghi hình trẻ em, dữ liệu giọng nói là dữ liệu nhạy cảm theo khung pháp luật bảo vệ DLCN tại VN | Consent record có version, theo từng mục đích (ghi hình / AI chấm điểm / marketing); với học viên < 16 tuổi cần đồng ý của phụ huynh; retention policy tự xóa recording sau N tháng |
| P10 | **Doanh thu chưa thực hiện** | Kế toán phải ghi nhận doanh thu theo buổi đã dạy, không theo tiền thu | Credit ledger cho phép xuất báo cáo *deferred revenue* theo kỳ một cách chính xác |
| P11 | **Hoàn tiền & khuyến mãi phức tạp** | Gói 48 buổi tặng 4 buổi, hoàn tiền thì tính buổi tặng thế nào | Credit có `unit_amount` thực tế (tiền thu / số buổi tính phí) và loại credit `Paid` vs `Bonus`; policy hoàn tiền tiêu thụ `Bonus` trước hay sau là cấu hình |
| P12 | **Rủi ro chia sẻ tài khoản pool** | Đưa password host account cho GV = mất kiểm soát, vi phạm bảo mật | GV **không bao giờ đăng nhập** host account; GV tham gia bằng tài khoản riêng và được nâng quyền co-host (cần xác minh API); host account chỉ là "chủ sở hữu phòng" do hệ thống vận hành |
| P13 | **Thu tiền một cơ sở, học ở cơ sở khác** | Doanh thu/KPI cơ sở bị sai, tranh cãi nội bộ | `branch_id` snapshot trên mọi giao dịch — chi tiết [7.5.3](07-multi-tenant-multi-language.md#753-các-bài-toán-đa-cơ-sở-riêng) |
| P14 | **GV dạy nhiều cơ sở / nhiều trung tâm** | Xếp liền 2 ca ở 2 cơ sở xa nhau; GV freelance dạy cho trung tâm khác | Buffer di chuyển theo cặp cơ sở; identity toàn nền tảng nhưng hồ sơ GV riêng từng tenant, không lộ lịch chéo |
| P15 | **Lịch nghỉ lễ thay đổi theo năm** | Tết âm lịch, nghỉ bù — khóa học bị lệch ngày kết thúc | Lịch nghỉ là dữ liệu, không phải quy tắc tính — chi tiết [7.5.3](07-multi-tenant-multi-language.md#753-các-bài-toán-đa-cơ-sở-riêng) |
| P16 | **"Level" khác nhau giữa các ngôn ngữ** | Hard-code A1–C2 thì không dạy được tiếng Nhật N5–N1, HSK, TOPIK | Bảng khung năng lực + level + kỹ năng; năng lực GV theo ngôn ngữ & level |
| P17 | **Phụ huynh/GV dùng ngôn ngữ khác nhau** | GV bản ngữ không đọc được giao diện tiếng Việt; phụ huynh không đọc được nhận xét tiếng Anh của GV | Tách UI locale / ngôn ngữ giảng dạy / ngôn ngữ liên lạc; nút dịch AI cho nhận xét |

## 1.4 Lưu ý chiến lược về Host Account Pooling

Đây là ý tưởng tiết kiệm chi phí hấp dẫn nhưng cần nhìn thẳng rủi ro:

1. **Điều khoản cấp phép**: license Google Workspace cấp *theo người dùng*. Dùng account như "phòng học" do hệ thống vận hành (không giao cho người cụ thể) là vùng xám — **cần xác nhận với Google reseller** trước khi scale. Thiết kế Konnyaku phải để **Room Provider là abstraction** (`IVirtualRoomProvider`) để có thể chuyển sang mô hình "mỗi GV một license" hoặc Zoom/Teams mà không đập lại hệ thống.
2. **Bài toán kinh tế**: số license cần = **số buổi học đồng thời tối đa (peak concurrency)**, không phải số GV. Trung tâm 40 GV nhưng peak 12 lớp cùng lúc lúc 19h → 12–14 license (thêm buffer). Konnyaku nên có **báo cáo peak concurrency** để chủ trung tâm tự thấy số tiền tiết kiệm.
3. **Pool thuộc từng tenant**: mỗi trung tâm kết nối domain Google Workspace của chính họ; Konnyaku không bao giờ dùng chung host account giữa các trung tâm (xem [7.4.5](07-multi-tenant-multi-language.md#745-tích-hợp-bên-ngoài-là-của-tenant)).
4. **Peak giờ vàng** (18h–21h ngày thường): chính khung giờ này quyết định số license. Tính năng "gợi ý dời lớp sang giờ thấp điểm kèm ưu đãi học phí" giúp giảm license — một ví dụ AI/automation tạo ra tiền thật.
