# 10 — Học tập và đánh giá: đặt buổi, điểm danh, bài kiểm tra, bài tập, tài liệu, nhắn tin

Thiết kế cho nhóm tính năng học tập áp dụng chung cho **lớp online và lớp tại cơ sở**. Tài liệu gồm:

- hiện trạng code;
- thiết kế từng tính năng;
- các đề xuất thêm;
- thứ tự làm và ước lượng.

Phạm vi và quy tắc nền vẫn theo [04-roadmap](04-roadmap.md), [07-multi-tenant-multi-language](07-multi-tenant-multi-language.md) và [09-ra-soat-va-ke-hoach-production](09-ra-soat-va-ke-hoach-production.md). Tài liệu này thay các mục P10, P13, P14, P15, P16 và B1 của doc 09 bằng thiết kế chi tiết.

## 10.1 Hiện trạng (main, 25/11/2026)

| Tính năng | Đã có | Còn thiếu |
| --- | --- | --- |
| Đặt buổi, dời buổi | `Class.Capacity`, `EnrolledCount`, `DeliveryMode` (Online/Offline/Hybrid). Ghi danh giữ chỗ nguyên tử (`academic.class_full`). `Session.LessonId`. Enum `EnrollmentType.MakeupOnly` có nhưng chưa dùng | Chỗ ngồi theo từng buổi (`session_seat` mới chỉ có trong doc 03). Học viên chưa thể xin nghỉ, đặt buổi bù hay chọn học online hoặc tại cơ sở. Toàn bộ API lịch là của nhân viên |
| Điểm danh | 6 trạng thái, nguồn Auto/Manual/Override. Lớp online có bản nháp tự động từ danh sách người vào phòng. Giáo viên xác nhận cả buổi một lần. Chỉ dòng đã xác nhận và tính phí mới trừ buổi (sổ append-only) | Học viên tự điểm danh. Xác nhận từng học viên. Trang lịch sử điểm danh của học viên |
| Bài kiểm tra, thi thử | Chỉ có khung trình độ và kỹ năng trong Catalog (`ProficiencyFramework`, `SkillDimension`) | Toàn bộ |
| Bài tập về nhà | Chỉ có thiết kế ở doc 03 | Toàn bộ, kể cả chấm chéo |
| Tài liệu | Không có kho file. `Recording.FileRef` là chuỗi giả. Đã bỏ MinIO khỏi compose | Kho file, tài liệu theo lớp và bài, chia sẻ giữa học viên |
| Nhắn tin, thông báo | Module Notification gửi email theo 8 mẫu. Chat trong phòng học WebRTC qua DataChannel, mất khi đóng phòng | Hộp thông báo trong app, đẩy thời gian thực, nhắn tin lưu lại |

## 10.2 Nguyên tắc thiết kế

1. **Một bộ máy đánh giá cho mọi loại bài.** Bài tập về nhà, bài kiểm tra nhanh, bài kiểm tra định kỳ, thi thử và kiểm tra xếp lớp dùng chung một module `Assessment`. Chúng chỉ khác cấu hình: thời gian, số lần làm, ai chấm, khi nào công bố điểm. Nhờ vậy chỉ cần làm một trình soạn câu hỏi, một màn làm bài và một hàng đợi chấm.
2. **File không đi qua API.** Trình duyệt tải lên và tải xuống thẳng kho S3 bằng URL ký sẵn, nên API không giữ file trong RAM (theo [toi-uu-tai-nguyen](toi-uu-tai-nguyen.md)). Module khác chỉ lưu `file_id`.
3. **Thời gian thực chỉ để giao tin, không để lưu.** Tin nhắn và thông báo luôn ghi vào PostgreSQL trước, qua outbox. Kênh WebSocket chỉ báo "có mới". Khi kết nối lại, client tải phần còn thiếu theo con trỏ, nên mất kết nối không mất dữ liệu.
4. **Chỗ ngồi theo buổi là bản ghi thật.** Danh sách học viên của một buổi lấy từ bảng đặt chỗ, không suy ra từ danh sách ghi danh. Nhờ vậy dời buổi, học bù và lớp hybrid đều dùng chung một cơ chế.
5. **Quy tắc theo trung tâm.** Hạn xin nghỉ, số lần dời, ai được nhắn cho ai, chấm chéo bật hay tắt đều là cài đặt của tenant, có giá trị mặc định an toàn. Đây cũng là mục P7 của doc 09.

## 10.3 Kho file (nền cho bài kiểm tra, bài tập, tài liệu, tin nhắn)

**Module mới `Content`** (schema `content`, role `kk_content`):

| Bảng | Vai trò |
| --- | --- |
| `stored_file` | `id`, `tenant_id`, `owner_user_id`, `purpose` (QuestionMedia, Submission, Material, ChatAttachment, Avatar), `content_type`, `size`, `sha256`, `status` (Pending, Ready, Quarantined, Deleted), `object_key` |
| `folder`, `material` | Thư viện tài liệu: cây thư mục theo trung tâm, lớp và bài học (`curriculum_lesson_id`) |
| `share` | Ai được xem: một lớp, một nhóm người, hoặc liên kết có hạn |
| `tenant_quota` | Dung lượng đã dùng và giới hạn theo gói |

**Luồng tải lên:**

1. Client gọi `POST /api/content/uploads` với tên, loại và kích thước file. API kiểm tra danh sách loại được phép, giới hạn kích thước và quota, rồi trả URL `PUT` ký sẵn (hết hạn sau 10 phút) cùng `file_id` ở trạng thái Pending.
2. Trình duyệt `PUT` thẳng lên kho.
3. Client gọi `POST /api/content/uploads/{id}/complete`. API kiểm tra object thật trên kho (kích thước, loại theo magic bytes) rồi chuyển sang Ready.
4. Worker dọn các file Pending quá 24 giờ.

**Tải xuống:** API kiểm tra quyền rồi trả URL `GET` ký sẵn có hạn 5 phút. Không bao giờ có URL công khai cố định.

**Kho lưu trữ:**

- Interface `IObjectStorage` ở BuildingBlocks.
- Dev và test dùng `LocalDiskStorage`: thư mục trên máy, API tự phục vụ URL ký.
- Production dùng S3: tự chạy **Garage** (viết bằng Rust, ít RAM, có API S3) trong `infra/deploy/compose.yaml`, đặt sau Caddy ở `files.<domain>`. Khi lớn lên thì đổi sang object storage của nhà cung cấp VPS (Viettel, BizFly, FPT) chỉ bằng biến môi trường.
- Backup kho đi cùng backup DB (phần C2).

**An toàn:**

- Chỉ nhận loại file trong danh sách cho phép: ảnh, âm thanh, PDF, Office, video ngắn.
- File học viên tải lên chưa hiện cho người khác cho đến khi quét xong. ClamAV chạy ở profile tuỳ chọn trên máy 8 GB; máy 2 GB thì tắt và chỉ cho chia sẻ PDF và ảnh.
- Ảnh được xoá EXIF (vị trí GPS) khi xử lý. Đây là dữ liệu cá nhân, theo [xu-ly-du-lieu-ca-nhan](xu-ly-du-lieu-ca-nhan.md).

## 10.4 Đặt buổi, dời buổi, học bù, chọn online hoặc tại cơ sở

### Mô hình

Thêm vào module Scheduling (chỗ ngồi gắn với buổi học):

| Bảng | Nội dung |
| --- | --- |
| `session_seat` | `session_id`, `online_capacity`, `offline_capacity`, `online_booked`, `offline_booked`, `makeup_capacity`. Chỗ tại cơ sở không vượt quá sức chứa phòng |
| `session_booking` | `session_id`, `student_id`, `enrollment_id`, `mode` (Online, Offline), `kind` (Regular, Makeup, Flexible, Trial), `status` (Booked, Postponed, Cancelled, Attended, NoShow), `source_booking_id` (buổi gốc nếu là học bù) |
| `makeup_entitlement` | Quyền học bù sinh ra khi xin nghỉ đúng hạn: `lesson_id`, `expires_at`, `used_by_booking_id` |

**Tạo đặt chỗ:**

- Khi ghi danh được kích hoạt, Scheduling nhận `EnrollmentActivatedV1` rồi tạo đặt chỗ `Regular` cho mọi buổi tương lai của lớp.
- Khi lớp thêm buổi (`Replan`), sinh tiếp đặt chỗ cho các buổi mới.
- Lớp hybrid đặt mặc định theo hình thức học viên chọn lúc ghi danh.

**Giữ chỗ nguyên tử:** `UPDATE session_seat SET offline_booked = offline_booked + 1 WHERE … AND offline_booked < offline_capacity`, giống cách ghi danh đang giữ chỗ. Có thêm danh sách chờ: khi có người huỷ, Worker mời người đầu hàng chờ và giữ chỗ cho người đó 2 giờ.

### Học viên làm được gì (khu vực `learn`)

| Hành động | Quy tắc mặc định (trung tâm đổi được) |
| --- | --- |
| **Xin nghỉ, dời buổi** | Trước giờ học ít nhất 12 giờ: buổi chuyển `Postponed`, không trừ buổi, sinh quyền học bù hạn 30 ngày. Sau hạn đó: tính là vắng (vẫn trừ buổi như hiện nay). Tối đa 3 lần cho mỗi khoá |
| **Đặt buổi bù** | Chọn trong các buổi **cùng bài học** (`lesson_id`) ở lớp khác cùng chương trình và trình độ, còn chỗ học bù, trong hạn của quyền học bù. Nếu không có buổi cùng bài thì giáo vụ duyệt tay |
| **Đổi hình thức học một buổi** (lớp hybrid) | Đổi từ tại cơ sở sang online hoặc ngược lại nếu hình thức kia còn chỗ, trước giờ học 2 giờ |
| **Lớp linh hoạt** (`BookingMode = Flexible`) | Lớp không có danh sách cố định: học viên mua gói buổi rồi tự đặt từng buổi như đặt lịch phòng gym. Hợp với lớp giao tiếp và câu lạc bộ nói |

### Giáo viên và giáo vụ

- Giáo viên xem danh sách buổi theo đặt chỗ, có đánh dấu học viên học bù và hình thức online hoặc tại cơ sở.
- Giáo vụ có màn duyệt yêu cầu học bù ngoại lệ, xếp tay và xem học viên dời nhiều lần (cảnh báo sắp bỏ học, mục B1 của doc 08).

### Liên quan sổ buổi học

- Không đổi cách tính: chỉ **điểm danh đã xác nhận và tính phí** mới trừ buổi.
- Buổi đã dời đúng hạn không có dòng điểm danh nên không trừ.
- Buổi học bù trừ một buổi khi điểm danh, với trạng thái `Makeup` (đã có trong enum).

## 10.5 Điểm danh: giáo viên điểm danh, học viên tự điểm danh rồi giáo viên xác thực

Thêm vào module Learning:

- **Bảng `attendance_claim`**: học viên báo có mặt, gồm `method` (Qr, Link, Auto) và `claimed_at`. Đây chỉ là đề nghị, chưa phải điểm danh chính thức.
- **Lớp tại cơ sở**: màn hình giáo viên chiếu một **mã QR đổi mỗi 30 giây**, là HMAC của buổi học và khung thời gian. Học viên quét bằng điện thoại khi đã đăng nhập khu vực `learn`, trong khung từ 15 phút trước giờ học đến 20 phút sau giờ bắt đầu. Mã đổi liên tục nên chụp gửi cho bạn ở nhà sẽ hết hạn.
- **Tuỳ chọn kiểm tra vị trí:** trình duyệt gửi toạ độ, API chỉ lưu kết quả đạt hoặc không đạt, không lưu toạ độ vì đây là dữ liệu cá nhân.
- **Lớp online**: giữ bản nháp tự động từ danh sách người vào phòng như hiện nay.
- **Màn xác nhận của giáo viên**:
  - Mỗi học viên một dòng, có nhãn "học viên đã báo có mặt lúc 18:02 qua QR".
  - Giáo viên sửa trạng thái từng dòng, xác nhận từng dòng hoặc cả buổi.
  - Hệ thống nhắc giáo viên nếu buổi đã xong 2 giờ mà chưa xác nhận.
  - Sau 48 giờ, giáo vụ mới được xác nhận thay.
- **Trang "Lịch sử điểm danh"** cho học viên và phụ huynh: từng buổi, trạng thái, số buổi còn lại. Đây là mục P10 của doc 09.
- Ngưỡng có mặt, đi muộn và vắng (hiện viết cứng 70%, 10 phút, 30%) chuyển thành cài đặt của trung tâm (P7).

## 10.6 Bài kiểm tra, thi thử, kiểm tra xếp lớp (module mới `Assessment`)

### Ngân hàng câu hỏi

| Bảng | Nội dung |
| --- | --- |
| `question_bank` | Ngân hàng theo trung tâm, chia theo ngôn ngữ và chương trình. Có quyền xem và quyền sửa |
| `question` | `type`, `stem` (văn bản định dạng), `media` (danh sách `file_id`), `options`, `answer_key`, `points`, `skill` (`SkillDimension`), `level` (`ProficiencyLevel`), `tags`, `version` |
| `question_group` | Nhóm câu dùng chung một đoạn đọc hoặc một file nghe (dạng bài đọc hiểu, nghe hiểu) |

**Các dạng câu hỏi theo 4 kỹ năng:**

| Dạng | Chấm | Ghi chú |
| --- | --- | --- |
| Một đáp án, nhiều đáp án, đúng sai | Máy | Trộn thứ tự đáp án |
| Điền vào chỗ trống | Máy | Nhiều đáp án chấp nhận; tuỳ chọn bỏ dấu, bỏ hoa thường, chấp nhận cả kana và kanji |
| Nối cặp, sắp xếp câu hoặc từ | Máy | Hợp với ngữ pháp tiếng Nhật và tiếng Trung (sắp xếp ★ kiểu JLPT) |
| Trả lời ngắn | Máy gợi ý, giáo viên duyệt | So khớp với danh sách đáp án, lệch thì vào hàng chờ chấm |
| **Nghe** | Theo dạng câu con | File âm thanh có giới hạn số lần nghe, mặc định 1 lần như thi thật. Giới hạn này chỉ ở phía client, ghi rõ cho giáo viên biết |
| **Đọc** | Theo dạng câu con | Nhóm câu có đoạn văn |
| **Viết** (tự luận) | Giáo viên, theo thang tiêu chí | Đếm số từ hoặc số ký tự (với tiếng Nhật và tiếng Trung), giới hạn tối thiểu và tối đa |
| **Nói** | Giáo viên, theo thang tiêu chí | Ghi âm trong trình duyệt (`MediaRecorder`, opus), có thời gian chuẩn bị và thời gian nói như thi IELTS hoặc TOPIK, tải lên kho file |

**Soạn câu hỏi cho tiếng Nhật và tiếng Trung:** trình soạn có **furigana và pinyin** bằng thẻ `<ruby>`. Tuỳ chọn hiện hoặc ẩn furigana khi làm bài, vì bài JLPT không có furigana trên kanji cần kiểm tra.

**Phiên bản câu hỏi:** bài làm lưu bản chụp phiên bản câu hỏi lúc làm. Sửa câu hỏi sau đó không làm đổi kết quả cũ.

### Nhập từ file

| Định dạng | Cách làm |
| --- | --- |
| **Excel hoặc CSV** (làm trước) | Mẫu tải về từ hệ thống, mỗi dòng một câu: dạng, đề, đáp án A–F, đáp án đúng, điểm, kỹ năng, trình độ, tên file media. Media đi kèm trong một file `.zip` |
| **Word (.docx)** | Theo quy ước đơn giản: `Câu 1.`, các dòng `A.` đến `D.`, đáp án đúng đánh dấu `*` hoặc in đậm, đoạn đọc mở đầu bằng `[Đoạn văn]`. Ảnh trong Word được tách thành file media |
| QTI 2.1, Moodle GIFT, Aiken | Để sau, khi trung tâm chuyển từ Moodle sang |

**Luồng nhập:**

1. Tải file lên kho.
2. Worker phân tích và trả **bản xem trước**: số câu hợp lệ và danh sách lỗi theo dòng hoặc đoạn, ví dụ "Dòng 12: thiếu đáp án đúng".
3. Người nhập bấm xác nhận thì mới ghi vào ngân hàng.

Nhập lại cùng một file không tạo câu trùng (so `sha256` của nội dung câu).

### Đề và giao bài

- **`assessment` (đề)** gồm các phần theo kỹ năng, mỗi phần có thời gian riêng như JLPT hay IELTS. Câu hỏi cố định hoặc rút ngẫu nhiên từ ngân hàng theo thẻ, kỹ năng và trình độ, ví dụ "rút 10 câu ngữ pháp N4". Mỗi học viên nhận một đề khác, giảm chép bài.
- **`assignment` (giao bài)** giao một đề cho một lớp, một buổi học hoặc vài học viên. Thiết lập:

| Thiết lập | Giá trị |
| --- | --- |
| Loại | `Homework`, `Quiz`, `Test`, `MockExam`, `Placement` |
| Thời gian | Mở lúc, hạn nộp, nộp muộn bị trừ điểm hay khoá |
| Số lần làm | 1, n lần hoặc không giới hạn (tính điểm cao nhất, lần cuối hoặc trung bình) |
| Ai chấm | `Auto`, `Teacher`, `Peer` cộng `Teacher` (xem 10.7) |
| Công bố điểm | Ngay khi nộp, khi giáo viên chấm xong, hoặc vào một thời điểm cho cả lớp (tránh lộ đáp án khi còn người chưa làm) |
| Xem đáp án | Không, sau khi nộp, hoặc sau hạn |

### Làm bài trên app (khu vực `learn`)

- **Thời gian đếm ở server:** `attempt.deadline_at` do server đặt. Hết giờ thì server tự nộp, dù client có tắt máy.
- Bài làm **tự lưu mỗi vài giây**. Mất mạng hoặc đổi máy vẫn làm tiếp được.
- Chỉ một lượt làm đang mở cho mỗi bài.
- **Ghi nhận rời tab:** tab bị ẩn và dán văn bản được ghi lại cho giáo viên xem, không chặn. Không hứa chống gian lận tuyệt đối với bài làm ở nhà.
- **Thi có giám sát** (để sau): bài thi thử quan trọng có thể chạy trong phòng học WebRTC, giáo viên xem camera.
- Giao diện dùng tốt trên điện thoại: một câu một màn, có bảng điều hướng câu và đánh dấu câu cần xem lại.

### Chấm và xem điểm

- Câu chấm bằng máy có điểm ngay lúc nộp.
- Câu tự luận và câu nói vào **hàng chờ chấm của giáo viên**:
  - Lọc theo lớp và hạn.
  - Chấm theo thang tiêu chí, ví dụ IELTS Writing có 4 tiêu chí × band 0–9.
  - Nhận xét bằng chữ hoặc ghi âm.
  - Đánh dấu và chú thích ngay trên bài viết.
- Học viên có trang **"Kết quả"**: điểm theo từng kỹ năng, câu sai kèm đáp án và giải thích (theo thiết lập công bố), và tiến bộ qua các bài.
- Điểm chính thức phát `AssessmentGradedV1`. Sổ điểm (gradebook, mục 10.10) và thông báo đều nghe event này.

### Thi thử theo chuẩn

- **Bảng quy đổi** từ điểm thô sang điểm chuẩn đặt theo khung trình độ trong Catalog:
  - IELTS Listening và Reading quy đổi theo số câu đúng ra band.
  - JLPT có điểm tối thiểu cho từng phần và tổng điểm đỗ.
  - HSK và TOPIK có thang riêng.
- **Báo cáo thi thử** cho học viên và phụ huynh: điểm từng phần, đỗ hay trượt theo chuẩn, so với lần trước, gợi ý kỹ năng cần luyện.
- Chế độ **chấm máy toàn phần** (chỉ dùng câu khách quan) cho kết quả ngay. Chế độ **gửi giáo viên chấm** cho đề có viết và nói.

### Kiểm tra xếp lớp

Dùng cùng bộ máy, giao cho **lead** trước khi thành học viên (mục B4 của doc 08):

- Làm qua liên kết có hạn, không cần tài khoản.
- Kết quả gợi ý trình độ và lớp còn chỗ.
- Tư vấn viên thấy kết quả ngay trên lead.

## 10.7 Bài tập về nhà và chấm chéo

Bài tập về nhà là một `assignment` loại `Homework` gắn với buổi học:

- Giáo viên giao ngay trên màn buổi học, có sẵn đề theo bài học trong giáo trình.
- Học viên nộp bằng câu trả lời trên app, file (ảnh vở viết tay, PDF) hoặc ghi âm.

**Chấm chéo (`Peer`):**

1. Giáo viên bật chấm chéo cho bài.
2. Sau hạn nộp, hệ thống **phân ngẫu nhiên** mỗi bài cho 1–2 bạn cùng lớp. Mặc định ẩn danh hai chiều; giáo viên có thể cho hiện tên.
3. **"Nhờ bạn chấm":** học viên cũng có thể chủ động mời một bạn cùng lớp chấm bài của mình. Bạn được mời có quyền từ chối.
4. Người chấm dùng cùng thang tiêu chí của giáo viên và phải viết nhận xét tối thiểu, ví dụ 20 từ.
5. **Điểm của bạn chỉ là tham khảo.** Giáo viên xem điểm và nhận xét của bạn rồi chốt điểm chính thức. Với bài luyện tập không tính điểm, giáo viên có thể cho phép lấy luôn điểm bạn chấm.
6. Học viên có thể đánh giá nhận xét của bạn là "hữu ích" hoặc "không hữu ích". Giáo viên thấy ai chấm cẩu thả.
7. Chấm cho bạn được ghi nhận vào điểm chuyên cần hoặc điểm tích cực. Phần này tuỳ trung tâm.

**Bảo vệ học viên nhỏ tuổi:** trung tâm có thể tắt chấm chéo cho lớp thiếu nhi. Mọi nhận xét chấm chéo giáo viên đều xem được và ẩn được.

## 10.8 Tài liệu và chia sẻ giữa học viên

**Thư viện của trung tâm (giáo viên và giáo vụ):**

- Tài liệu theo chương trình, bài học (`curriculum_lesson`) và lớp.
- Tài liệu gắn với bài học tự hiện cho mọi lớp học bài đó, nên giáo viên không phải tải lên lại cho từng lớp.
- Quyền tải về hay chỉ xem. File PDF chỉ xem hiển thị trong trình duyệt có hình mờ (watermark) tên người xem, để hạn chế phát tán giáo trình có bản quyền.

**Góc chia sẻ của lớp (học viên):**

- Học viên đăng file hoặc liên kết lên không gian lớp, ví dụ ghi chú, flashcard, bài mẫu.
- Ai trong lớp cũng xem được. Giáo viên ẩn hoặc xoá được.
- Trung tâm chọn chế độ đăng ngay hoặc chờ giáo viên duyệt (mặc định chờ duyệt cho lớp thiếu nhi).
- Có nút báo cáo nội dung không phù hợp.

**Giới hạn:** dung lượng theo gói của trung tâm và kích thước tối đa mỗi file, ví dụ 20 MB cho học viên và 200 MB cho giáo viên (video bài giảng ngắn).

## 10.9 Nhắn tin và thông báo thời gian thực

### Hộp thông báo trong app (module Notification)

- **Bảng `inbox_item`** theo người dùng: `kind`, `title_key`, `params`, `link`, `read_at`. Tiêu đề lưu dạng mã i18n như mọi lỗi trả về, để hiển thị đúng tiếng Việt hay tiếng Anh.
- Mỗi sự kiện đi qua **bộ định tuyến kênh**: luôn vào hộp thông báo, cộng thêm email hoặc **Web Push** (PWA, chuẩn VAPID, không cần dịch vụ ngoài) theo cài đặt của người dùng.
- Có **gom tin**: nhiều thông báo cùng loại trong 10 phút gộp làm một email.
- Biểu tượng chuông có số chưa đọc ở cả 3 khu vực. Có trang cài đặt nhận thông báo.
- Zalo ZNS để giai đoạn 4 (mục P20 của doc 09).

**Sự kiện có thông báo:**

- Có bài tập mới, sắp hết hạn nộp.
- Có điểm, được mời chấm chéo.
- Buổi học đổi giờ hoặc bị huỷ, có chỗ trống từ danh sách chờ.
- Yêu cầu học bù được duyệt.
- Có tin nhắn mới.
- Tài liệu mới trong lớp.

### Nhắn tin (module mới `Messaging`)

| Bảng | Nội dung |
| --- | --- |
| `conversation` | `kind`: `Direct` (1-1), `ClassGroup` (tự tạo theo lớp, thành viên đồng bộ theo ghi danh), `Support` (học viên hoặc phụ huynh với giáo vụ của cơ sở) |
| `conversation_member` | Vai trò trong nhóm, con trỏ đã đọc, tắt thông báo |
| `message` | Nội dung văn bản, `file_id` đính kèm, trả lời tin nào, `deleted_at` (xoá mềm). Chỉ sửa được trong 15 phút |

**Ai được nhắn cho ai** (mặc định an toàn, trung tâm đổi được):

| Người gửi | Được nhắn trực tiếp |
| --- | --- |
| Học viên | Giáo viên các lớp mình đang học, giáo vụ cơ sở. Nhắn bạn cùng lớp: **tắt mặc định** |
| Phụ huynh | Giáo viên của con, giáo vụ |
| Giáo viên | Học viên và phụ huynh của lớp mình, đồng nghiệp |
| Nhân viên vận hành | Mọi người trong phạm vi cơ sở của mình |

**An toàn:**

- Nhóm lớp có giáo viên làm quản trị.
- Có nút báo cáo tin nhắn.
- Quản lý trung tâm được xem lại nhóm lớp và các tin bị báo cáo. Mọi lần xem đều ghi audit.
- Thời gian lưu tin là cài đặt của trung tâm, mặc định 2 năm.

### Hạ tầng thời gian thực

Thay vì thêm SignalR vào API (tốn RAM, và nhiều bản sao thì phải có backplane), **mở rộng service Rust `services/signaling` thành `realtime`**:

- **Kết nối:** trình duyệt mở **một** WebSocket `/rt` cho mỗi tab. Xác thực bằng vé ngắn hạn mà API ký, giống cách phòng học đang làm.
- **Phát tin:**
  1. Worker xử lý outbox.
  2. Worker `PUBLISH` lên Redis vào kênh `user:{id}`.
  3. Service `realtime` (subscribe Redis) đẩy xuống các kết nối của người đó.
  Chạy nhiều bản sao không cần gì thêm.
- **Nội dung tin đẩy:** chỉ gồm loại và id, ví dụ `{"t":"message","conv":"…","seq":812}`. Client tự gọi API lấy nội dung, nên không có dữ liệu nào đi qua kênh mà không kiểm tra quyền.
- **Kết nối lại:** client gọi `GET /api/messaging/sync?since=<cursor>` để lấy phần lỡ.
- **Ngoài phạm vi:** trạng thái "đang gõ" và "đang online" đi thẳng qua `realtime`, không ghi DB.
- **Phòng học:** chat trong phòng học giữ DataChannel như hiện nay, nhưng có thêm tuỳ chọn lưu vào nhóm lớp khi buổi học kết thúc.

## 10.10 Đề xuất thêm

| # | Đề xuất | Vì sao | Công sức |
| --- | --- | --- | --- |
| N1 | **Sổ điểm và báo cáo tiến bộ theo kỹ năng** (biểu đồ radar nghe, nói, đọc, viết; nhận xét định kỳ; gửi phụ huynh). Thay mục P16 | Phụ huynh trả tiền muốn thấy con tiến bộ. Đây là lý do giữ chân học viên số một | 2 tuần |
| N2 | **Từ vựng và flashcard lặp lại ngắt quãng** (SRS), sinh từ danh sách từ của từng bài học; kanji, hanzi có nét viết | Đặc thù học ngoại ngữ, giúp học viên mở app mỗi ngày chứ không chỉ hôm có lớp | 2,5 tuần |
| N3 | **AI hỗ trợ chấm**: gợi ý điểm và nhận xét bài viết theo thang IELTS, JLPT; chấm phát âm bài nói. Giáo viên luôn duyệt trước khi học viên thấy | Giảm thời gian chấm tự luận, vốn là nút thắt khi lớp đông. Chi phí theo lượt, bật theo gói | 2 tuần |
| N4 | **Chứng chỉ hoàn thành có mã QR xác minh** (trang công khai kiểm tra chứng chỉ thật) | Trung tâm cần cấp khi xong khoá; QR chống làm giả | 1 tuần |
| N5 | **Lịch cá nhân dạng ICS** (thêm vào Google Calendar hay lịch điện thoại), tự cập nhật khi đổi giờ | Học viên và giáo viên dùng lịch điện thoại, ít khi mở app chỉ để xem lịch | 0,5 tuần |
| N6 | **Chuỗi ngày học, huy hiệu, bảng xếp hạng lớp** (nhẹ, tắt được) | Tăng tỉ lệ làm bài tập; tắt được cho lớp người lớn | 1 tuần |
| N7 | **Sổ liên lạc điện tử** sau mỗi buổi: giáo viên nhận xét nhanh từng học viên bằng mẫu có sẵn (mục B5 của doc 08) | Phụ huynh lớp thiếu nhi rất cần; dữ liệu cho báo cáo N1 | 1 tuần |
| N8 | **Ngân hàng câu hỏi dùng chung giữa các trung tâm** (chợ đề: bán hoặc chia sẻ) | Trung tâm nhỏ không có sẵn đề. Giá trị nền tảng cho Konnyaku | Giai đoạn sau |
| N9 | **Chế độ mạng yếu**: PWA lưu sẵn tài liệu, làm bài khi mất mạng rồi đồng bộ lại | Học viên dùng 4G yếu; bài làm không bị mất | 1,5 tuần |
| N10 | **Phân tích câu hỏi**: tỉ lệ đúng, độ phân biệt, câu bị chọn sai nhiều | Giáo viên sửa đề kém chất lượng; giáo vụ thấy lỗ hổng kiến thức chung của lớp | 1 tuần |

## 10.11 Thứ tự làm và ước lượng

**Phụ thuộc:**

- **Kho file** cần trước cho media câu hỏi, bài nộp dạng file, tài liệu và đính kèm tin nhắn.
- **Kênh thời gian thực** cần trước cho nhắn tin và chuông thông báo.
- **Đặt buổi** và **lõi Assessment** (câu khách quan và tự luận dạng chữ) không cần hai nền trên, nên làm song song được ngay.

| Đợt | Hạng mục | Module | Tuần công |
| --- | --- | --- | --- |
| **A** (song song) | A1 Kho file: module `Content`, `IObjectStorage`, Garage trong compose, URL ký, quota | Content (mới), BuildingBlocks, infra | 2 |
| | A2 Thời gian thực và hộp thông báo: service `realtime`, Redis pub/sub, `inbox_item`, chuông, Web Push (**đã làm**: service giữ tên và image `signaling`, thêm `/rt`; xem [trien-khai.md](trien-khai.md#thông-báo-trong-app-và-thời-gian-thực)) | Notification, services/signaling, frontend | 2,5 |
| | A3 Đặt buổi, dời buổi, học bù, chọn hình thức, danh sách chờ, lớp linh hoạt | Scheduling, Academic, Learning | 3,5 |
| | A4 Lõi Assessment: ngân hàng, câu khách quan và tự luận dạng chữ, đề, giao bài, làm bài, tự chấm, hàng chờ chấm, công bố điểm | Assessment (mới) | 5 |
| **B** (sau A) | B1 Tự điểm danh QR, xác nhận từng dòng, lịch sử điểm danh, ngưỡng theo trung tâm | Learning | 1,5 |
| | B2 Câu hỏi nghe và nói (media, ghi âm), furigana và pinyin, nhập Excel và Word | Assessment, Content | 3 |
| | B3 Bài tập về nhà và chấm chéo | Assessment | 2 |
| | B4 Thư viện tài liệu và góc chia sẻ của lớp | Content | 2 |
| | B5 Nhắn tin: trực tiếp, nhóm lớp, hỗ trợ; chính sách ai nhắn cho ai | Messaging (mới) | 3 |
| **C** | C1 Thi thử theo chuẩn và bảng quy đổi, kiểm tra xếp lớp cho lead | Assessment, Catalog, Crm | 2 |
| | C2 Sổ điểm và báo cáo tiến bộ (N1), sổ liên lạc (N7) | Learning, Assessment | 3 |
| | C3 Đề xuất N2–N10 theo ưu tiên của trung tâm thí điểm | | |

**Tổng đợt A–C: khoảng 29,5 tuần công.** Với 4–5 session song song mỗi đợt, đợt A và B mỗi đợt mất 2–3 tuần lịch.

**Lưu ý khi chạy song song:**

- A1 và A2 đều sửa `infra/deploy/compose.yaml`, nên phải chờ PR giám sát (C1/C3) và PR backup, deploy (C5/C2) của giai đoạn 1 merge trước.
- A3 và A4 không đụng compose nên bắt đầu được ngay.
- Thêm module `Content`, `Assessment`, `Messaging` phải cập nhật danh sách module theo quy tắc 9 trong `CLAUDE.md`. Ba PR này sẽ đụng cùng các file đó, nên merge lần lượt.
- Bài test giao diện Playwright (C6) cần thêm luồng cho mỗi tính năng: xin nghỉ và học bù, làm bài và chấm, gửi tin nhắn.
