# 9. Rà soát tính năng và kế hoạch lên production (11/2026)

Rà soát dựa trên code ở `main` (sau PR #3), dữ liệu review (`infra/scripts/seed_review.py`) và gọi thử API bằng tài khoản từng vai trò. Mã việc dạng `A1`, `B3`, `C4` trỏ tới [`08-de-xuat-cai-tien.md`](08-de-xuat-cai-tien.md); việc mới trong tài liệu này có mã `P…`. Khi một giai đoạn được chốt, đưa phạm vi của nó vào [`04-roadmap.md`](04-roadmap.md).

## 9.1 Kết luận

**Đủ để demo và chạy thử có người hỗ trợ; chưa đủ để giao cho một trung tâm tự vận hành.**

Phần mạnh nhất là vòng vận hành cốt lõi, đã chạy trọn với dữ liệu thật: khách đăng ký → lead → hồ sơ học viên → đơn hàng → thanh toán → tự ghi danh → xếp lịch → phòng học → điểm danh → trừ buổi → bảng công → kỳ lương. Nền kỹ thuật (đa tenant có RLS, sổ buổi học chỉ ghi thêm, chống trùng lịch ở DB, event qua outbox, deploy đa kiến trúc) vững.

Bốn nhóm thiếu chặn việc giao sản phẩm:

1. **Vòng đời tài khoản và phân quyền.** Không ai tự lấy lại được mật khẩu; nhân viên không đổi được mật khẩu; không khóa được tài khoản người đã nghỉ; không thu hồi được phiên đăng nhập. Giáo viên đọc được toàn bộ học viên và lead của trung tâm và đổi được trạng thái lead (đã kiểm chứng, xem 9.3).
2. **Không sửa được gì sau khi tạo** (đang làm) và **danh sách cắt ở 500 dòng** không có tìm kiếm.
3. **Phần "dạy và học" (LMS) gần như chưa có**: không tài liệu, bài tập, kiểm tra, điểm, nhận xét, báo cáo tiến độ. Hiện sản phẩm là phần mềm quản lý vận hành lớp học hơn là hệ thống học tập.
4. **Không có kênh liên lạc và báo cáo**: không email/Zalo/thông báo nào được gửi; không báo cáo doanh thu, công nợ, chuyên cần, sĩ số.

## 9.2 Theo từng vai trò

Ký hiệu: ✅ có và dùng được · 🟡 có một phần · ❌ chưa có.

### Super-admin (người vận hành nền tảng Konnyaku)

| Nhu cầu | Trạng thái | Ghi chú |
|---|---|---|
| Tạo trung tâm và quản trị viên đầu tiên | 🟡 | Chỉ qua API với `X-Platform-Key` (một khóa dùng chung, không gắn với người nào) |
| Bảng điều khiển nền tảng: danh sách trung tâm, số học viên/lớp, tình trạng | ❌ | Không có API liệt kê trung tâm, không có giao diện |
| Tạm khóa / mở lại / xóa trung tâm | ❌ | |
| Gói dịch vụ, giới hạn sử dụng, thu phí trung tâm | ❌ | |
| Hỗ trợ khách: xem với tư cách trung tâm (có ghi nhật ký) | ❌ | |
| Tài khoản super-admin riêng từng người, 2FA, nhật ký thao tác | ❌ | |
| Tên miền riêng của trung tâm | 🟡 | Bảng `tenant_domain` và HTTPS theo host đã sẵn; chưa có thao tác thêm tên miền |

### Quản trị trung tâm (TenantAdmin)

| Nhu cầu | Trạng thái | Ghi chú |
|---|---|---|
| Tạo cơ sở, phòng, ngày nghỉ, chương trình, cấp độ, lớp, nhân sự, gói học | ✅ | |
| Sửa / ngừng dùng các bản ghi trên | ❌ → đang làm | Session "sửa dữ liệu + phân quyền" |
| Cài đặt trung tâm: tên, logo, màu, ngôn ngữ, múi giờ, chính sách điểm danh | ❌ | Ngưỡng 70% / 10 phút / 30% đang cố định trong code |
| Quản lý tài khoản: đặt lại mật khẩu, khóa, đổi vai trò | ❌ | Trạng thái `Suspended` có trong enum nhưng không thao tác nào dùng |
| Phân quyền theo cơ sở | ❌ → đang làm | |
| Bảng điều khiển và báo cáo (doanh thu, công nợ, sĩ số, chuyên cần, tỉ lệ chuyển đổi lead) | 🟡 | 4 thẻ đếm ở trang chủ; API doanh thu ghi nhận có nhưng chưa có màn hình |
| Nhật ký thay đổi | ❌ → đang làm | |
| Kết nối phòng học (Meet / WebRTC), pool host | 🟡 | API có, chưa có màn hình trong `/ops` |
| Xuất dữ liệu (Excel/CSV), nhập hàng loạt | ❌ | |

### Quản lý cơ sở (BranchManager), Điều phối (Coordinator)

| Nhu cầu | Trạng thái | Ghi chú |
|---|---|---|
| Vai trò tồn tại, đăng nhập được | ✅ | |
| Quyền riêng của BranchManager | ❌ → đang làm | Hiện không endpoint nào dành cho vai trò này: BranchManager chỉ làm được việc mọi nhân viên làm được |
| Xếp lịch lớp, đổi giờ, đổi giáo viên, hủy buổi | 🟡 | API có đủ; giao diện mới có xếp lịch lần đầu |
| Lịch tổng theo ngày/tuần (theo giáo viên, phòng), phát hiện buổi thiếu giáo viên | ❌ | |
| Ghi danh tay, bảo lưu, cho nghỉ | 🟡 | API có, chưa có giao diện |
| Học bù, chuyển lớp | ❌ | B1 |
| Lịch rảnh giáo viên, dạy thay, duyệt xin nghỉ | ❌ | B3 |

### Tư vấn (Consultant)

| Nhu cầu | Trạng thái | Ghi chú |
|---|---|---|
| Nhận lead từ form công khai, chống trùng, gán tư vấn viên | ✅ | |
| Đổi trạng thái lead | ✅ | |
| Ghi chú cuộc gọi, lịch hẹn gọi lại, lịch sử chăm sóc | ❌ | |
| Chuyển lead thành học viên + đơn hàng trong một thao tác | 🟡 | Làm được qua 3 màn hình riêng |
| Đặt lịch test đầu vào / học thử | ❌ | B4 |
| Báo cáo theo kênh (UTM đã lưu) | ❌ | |

### Kế toán (Accountant)

| Nhu cầu | Trạng thái | Ghi chú |
|---|---|---|
| Gói học, đơn hàng, thanh toán nhiều lần | ✅ | |
| Sổ buổi học chỉ ghi thêm, điều chỉnh bằng bút toán | ✅ | Điều chỉnh qua API, chưa có giao diện |
| Xử lý hoàn tiền | ❌ | Đơn chuyển sang `RefundRequired` rồi dừng ở đó: không có thao tác ghi nhận đã hoàn |
| Phiếu thu / hóa đơn (in, PDF), hóa đơn điện tử | ❌ | B2 |
| Giảm giá, khuyến mãi, học phí theo đợt | ❌ | |
| Công nợ, nhắc phí | ❌ | |
| Kỳ lương, chốt kỳ | ✅ | |
| Phiếu lương PDF, phụ cấp / phạt | ❌ | |
| Thanh toán online | ❌ | B2 |

### Giáo viên

| Nhu cầu | Trạng thái | Ghi chú |
|---|---|---|
| Lịch dạy, vào lớp | ✅ | |
| Điểm danh (nháp tự động cho lớp online, tay cho offline) | ✅ | |
| Công tháng này | ✅ | |
| Đổi mật khẩu, hồ sơ cá nhân | ❌ | Không có API đổi mật khẩu cho nhân viên |
| Nội dung buổi học: giáo án, tài liệu, slide | ❌ | Mỗi buổi đã gắn một `curriculum_lesson` nhưng bài học chưa có nội dung |
| Giao bài tập, chấm, nhận xét học viên | ❌ | B5 |
| Nhập điểm kiểm tra, đánh giá cuối khóa | ❌ | |
| Báo lịch rảnh, xin nghỉ | ❌ | B3 |
| Xem lịch sử công và phiếu lương các kỳ trước | 🟡 | Chỉ tháng hiện tại |

### Học viên

| Nhu cầu | Trạng thái | Ghi chú |
|---|---|---|
| Lịch học, vào lớp | ✅ | |
| Số buổi còn lại, buổi tặng | ✅ | |
| Video xem lại | ✅ | Với provider Fake; ghi hình cho WebRTC chưa có (B6) |
| Đổi mật khẩu | ✅ | |
| Quên mật khẩu | ❌ | Phải nhờ trung tâm, mà trung tâm cũng chưa có nút đặt lại |
| Lịch sử điểm danh của mình | ❌ | |
| Lịch sử thanh toán, phiếu thu | ❌ | |
| Tài liệu, bài tập, nộp bài, điểm, nhận xét | ❌ | Giai đoạn 3 |
| Tiến độ theo cấp độ, chứng nhận hoàn thành | ❌ | |
| Xin nghỉ có phép, đăng ký học bù | ❌ | B1 |
| Thông báo nhắc giờ học, sắp hết buổi | ❌ | A2 |
| Cài như ứng dụng trên điện thoại | ❌ | B7 |

### Phụ huynh

| Nhu cầu | Trạng thái | Ghi chú |
|---|---|---|
| Đăng nhập | ✅ | |
| Xem lịch, chuyên cần, số buổi, nhận xét của con (nhiều con) | ❌ | Tài khoản phụ huynh chưa liên kết với học viên: trang `/learn` trống (A1) |
| Thanh toán, xem phiếu thu | ❌ | |

### Khách vãn lai (chưa đăng nhập)

| Nhu cầu | Trạng thái | Ghi chú |
|---|---|---|
| Trang giới thiệu theo trung tâm, 2 ngôn ngữ, URL dịch theo ngôn ngữ | ✅ | |
| Danh mục chương trình công khai | ✅ | |
| Form đăng ký tư vấn (có đồng ý xử lý dữ liệu, chống spam bằng honeypot + giới hạn tần suất) | ✅ | Chưa có captcha |
| Lịch khai giảng còn chỗ, học phí | ❌ | |
| Giới thiệu giáo viên, cơ sở, liên hệ, bản đồ | ❌ | |
| Đặt lịch test đầu vào / học thử | ❌ | B4 |
| Trung tâm tự sửa nội dung trang (CMS) | ❌ | |
| SEO: sitemap, robots, thẻ chia sẻ mạng xã hội | ❌ | |
| Trang chính sách quyền riêng tư, điều khoản | ❌ | Form có ghi phiên bản đồng ý nhưng không có trang nội dung |
| Trang lỗi 404 / 500 riêng | ❌ | Đang dùng trang mặc định của Next.js |

## 9.3 Lỗi cần sửa ngay (đã kiểm chứng)

| # | Lỗi | Bằng chứng |
|---|---|---|
| S1 | **Giáo viên đọc được toàn bộ học viên và lead (có SĐT, email) và đổi được trạng thái lead** | Token `teacher.ja@review.example.com`: `GET /api/crm/students` 200 (12 học viên cả trung tâm), `GET /api/crm/leads` 200, `POST /api/crm/leads/{id}/status` 204. Các endpoint này và ghi danh / bảo lưu / cho nghỉ chỉ kiểm "là nhân viên" (`Policies.Staff`). Đã chuyển cho session phân quyền |
| S2 | **Không thu hồi được phiên đăng nhập** | Token học viên sống 30 ngày, nhân viên 12 giờ; đăng xuất chỉ xóa cookie; không có danh sách thu hồi. Người bị cho nghỉ vẫn dùng được token đến khi hết hạn |
| S3 | **Không khóa được tài khoản** | `MembershipStatus.Suspended` không được thao tác nào đặt |
| S4 | **Endpoint mô phỏng buổi học tồn tại ở production** | `POST /api/workspace/dev/sessions/{id}/simulate` chỉ chặn theo provider (`Fake`), không theo môi trường: trung tâm đặt provider Fake có thể tự tạo dữ liệu tham gia, kéo theo điểm danh và công |

S2, S3, S4 đã sửa trong P2 + P3 (phase 11 ở README): phiên đăng nhập thu hồi được, khoá tài khoản, endpoint mô phỏng chỉ bật ở Development.

## 9.4 Thiếu gì để đạt chuẩn production

| Nhóm | Hiện có | Còn thiếu |
|---|---|---|
| Bảo mật tài khoản | Khóa sau 5 lần sai, cookie httpOnly, 2 realm tách biệt | Quên / đặt lại mật khẩu, đổi mật khẩu nhân viên, thu hồi phiên, 2FA cho quản trị và kế toán, đăng nhập Google (B8), captcha |
| Phân quyền | RLS theo tenant, policy theo realm | Theo vai trò cho mọi endpoint (S1), theo cơ sở, nhật ký thao tác |
| Dữ liệu cá nhân | Ghi nhận đồng ý ở form lead | Che dữ liệu theo vai trò, nhật ký xuất / in, chính sách lưu giữ và xóa, trang chính sách, thỏa thuận xử lý dữ liệu với nhà cung cấp hạ tầng (C4) |
| Độ tin cậy | Healthcheck, nhiều bản sao, migration tự động, smoke test 2 kiến trúc | Giám sát và cảnh báo (C3), backup khôi phục theo thời điểm + diễn tập (C5), deploy không gián đoạn (C2), giới hạn tần suất dùng chung (C1), môi trường staging thật |
| Chất lượng | 58 test backend, test kiến trúc, CI | Test giao diện tự động (C6), test tải, test bảo mật trước khi mở bán |
| Vận hành khách hàng | Hướng dẫn nhanh trong app, tài liệu chạy local / triển khai | Tài liệu hướng dẫn sử dụng đầy đủ, quy trình tiếp nhận trung tâm mới, kênh hỗ trợ, trang trạng thái dịch vụ |
| Liên lạc | Event đã phát sẵn | Dịch vụ gửi email, mẫu tin theo ngôn ngữ, Zalo ZNS (A2) |
| Pháp lý, tài chính | Sổ buổi học chỉ ghi thêm | Hóa đơn điện tử, điều khoản dịch vụ, hợp đồng với trung tâm |

## 9.5 Kế hoạch làm tất cả

Ước lượng tính bằng **tuần-người** (một người đã quen code làm một tuần). Các việc trong cùng giai đoạn ít chạm nhau nên chia được cho nhiều người hoặc nhiều session chạy song song: hai luồng song song thì thời gian theo lịch còn khoảng một nửa. Mỗi việc một nhánh, một PR, có test.

### Giai đoạn 0 — đang làm

| Việc | Ở đâu |
|---|---|
| Seed review, hướng dẫn nhanh, đề xuất | PR #4 (chờ merge) |
| Sửa / ngừng dùng bản ghi, phân quyền theo vai trò và cơ sở, nhật ký thay đổi, S1 | Session `ops-edit-permissions` |
| Phòng học WebRTC P2P (tối đa khoảng 4 người bật video) | Session `webrtc-classroom` |
| Đo và tối ưu RAM | Session `ram-optimization` |
| Tài liệu hướng dẫn sử dụng | Sau khi 2 PR đầu có |

### Giai đoạn 1 — An toàn để giao cho trung tâm đầu tiên (khoảng 10 tuần-người)

Điều kiện xong: một trung tâm thật dùng được mà không cần người của Konnyaku can thiệp vào database; mất mật khẩu, nhân viên nghỉ việc, hoàn tiền đều xử lý được trong app; sự cố được phát hiện và khôi phục được.

| # | Việc | Công |
|---|---|---|
| P1 | Dịch vụ gửi email + module `Notification` nền (mẫu theo `key, kênh, locale`; Mailpit ở dev) | 1 tuần |
| P2 | Vòng đời tài khoản: quên / đặt lại mật khẩu qua email, quản trị đặt lại mật khẩu, đổi mật khẩu nhân viên, khóa / mở tài khoản (S3), thu hồi phiên (S2), 2FA cho TenantAdmin và Accountant | 1,5 tuần |
| P3 | Siết endpoint mô phỏng theo môi trường (S4); rà toàn bộ endpoint theo ma trận vai trò sau PR phân quyền | 0,5 tuần |
| P4 | Hoàn tiền: ghi nhận đã hoàn, bút toán đảo trong sổ; phiếu thu in / PDF | 1 tuần |
| A3 | Tìm kiếm, lọc, phân trang cho mọi danh sách | 1 tuần |
| C4 | Bảo vệ dữ liệu cá nhân + trang chính sách quyền riêng tư và điều khoản | 1 tuần |
| C3, C5, C2, C1 | Giám sát và cảnh báo, backup theo thời điểm + diễn tập khôi phục, deploy cuốn chiếu, giới hạn tần suất qua Redis | 1,5 tuần |
| C6 | Test giao diện tự động cho 5 luồng chính | 1 tuần |
| P5 | Bảng điều khiển super-admin tối thiểu: danh sách trung tâm, tạo trung tâm + quản trị viên, tạm khóa, tài khoản riêng có 2FA | 1 tuần |
| P6 | Trang lỗi 404 / 500, captcha cho form công khai, sitemap và robots | 0,5 tuần |

### Giai đoạn 2 — Đủ dùng hằng ngày cho từng vai trò (khoảng 14 tuần-người)

Điều kiện xong: mỗi vai trò ở 9.2 làm được việc hằng ngày của mình mà không cần Excel hay Zalo bên ngoài cho các việc cốt lõi.

| # | Việc | Vai trò | Công |
|---|---|---|---|
| A1 | Liên kết phụ huynh – học viên, cổng phụ huynh | Phụ huynh | 1 tuần |
| A2 | Thông báo: nhắc lịch trước 24 giờ, sắp hết buổi, lead mới, tài khoản mới, đổi lịch / hủy buổi | Tất cả | 1 tuần |
| A4, A5 | Bảng điều khiển theo vai trò; báo cáo doanh thu, công nợ, sĩ số, chuyên cần, chuyển đổi lead; xuất Excel | Quản trị, kế toán, tư vấn | 1,5 tuần |
| A6 | Nhập học viên và lead từ CSV | Quản trị | 0,5 tuần |
| P7 | Cài đặt trung tâm: thông tin, logo, màu, chính sách điểm danh; màn hình kết nối phòng học và pool host | Quản trị | 1 tuần |
| P8 | Điều phối: lịch tổng theo tuần (giáo viên, phòng), giao diện đổi giờ / đổi giáo viên / hủy buổi, ghi danh tay, bảo lưu | Điều phối, quản lý cơ sở | 1,5 tuần |
| B1, B3 | Học bù, cảnh báo nghỉ; lịch rảnh, xin nghỉ, dạy thay | Điều phối, giáo viên, học viên | 2 tuần |
| P9 | CRM: ghi chú cuộc gọi, lịch hẹn, chuyển lead → học viên + đơn trong một thao tác | Tư vấn | 1 tuần |
| P10 | Học viên / giáo viên: lịch sử điểm danh, lịch sử thanh toán, hồ sơ cá nhân, công các kỳ trước | Học viên, giáo viên | 1 tuần |
| P11 | Trang công khai: lịch khai giảng còn chỗ, học phí, giáo viên, cơ sở, liên hệ; trung tâm tự sửa nội dung | Khách | 1,5 tuần |
| B4 | Đặt lịch test đầu vào / học thử | Khách, tư vấn | 1 tuần |
| P12 | Giảm giá, khuyến mãi, học phí theo đợt, nhắc phí | Kế toán | 1 tuần |

### Giai đoạn 3 — Dạy và học (LMS) (khoảng 12,5 tuần-người)

Điều kiện xong: giáo viên dạy theo giáo trình có sẵn trong hệ thống, giao và chấm bài; học viên và phụ huynh thấy kết quả và tiến độ.

| # | Việc | Công |
|---|---|---|
| P13 | Nội dung bài học: giáo án, tài liệu, tệp đính kèm theo `curriculum_lesson` (cần object storage) | 1,5 tuần |
| P14 | Bài tập: giao, nộp, chấm, trả bài | 1,5 tuần |
| P15 | Kiểm tra / quiz, ngân hàng câu hỏi cơ bản | 2 tuần |
| P16 | Sổ điểm theo kỹ năng của khung năng lực (đã có `skill_dimension`), báo cáo tiến độ, chứng nhận hoàn thành cấp độ | 1,5 tuần |
| B5 | Nhận xét sau buổi học, sổ liên lạc | 1 tuần |
| B6 | Ghi hình và lưu trữ video cho phòng học WebRTC | 2 tuần |
| B7 | PWA + thông báo đẩy cho `/learn`, `/teach` | 1 tuần |
| P17 | Phòng học cho lớp đông: thêm media server (SFU) khi lớp vượt 4 người bật video | 2 tuần |

### Giai đoạn 4 — SaaS thương mại (khoảng 11 tuần-người)

Điều kiện xong: trung tâm tự đăng ký, tự trả phí, dùng thương hiệu và tên miền riêng.

| # | Việc | Công |
|---|---|---|
| B2 | Thanh toán online (QR chuyển khoản, VNPay / MoMo), hóa đơn điện tử | 3 tuần |
| P18 | Gói dịch vụ, giới hạn sử dụng, thu phí trung tâm, tự đăng ký dùng thử | 2 tuần |
| P19 | Tên miền riêng, thương hiệu theo trung tâm, email gửi từ tên miền trung tâm | 1 tuần |
| B8 | Đăng nhập Google, đăng nhập tập trung `auth.` | 1,5 tuần |
| P20 | Zalo ZNS, kịch bản chăm sóc theo sự kiện | 1,5 tuần |
| P21 | Managed PostgreSQL, tách tenant lớn sang database riêng, test tải, kiểm thử bảo mật độc lập | 2 tuần |

### Tổng

Khoảng **47,5 tuần-người** sau giai đoạn 0 (10 + 14 + 12,5 + 11); với hai luồng làm song song là khoảng 6 tháng theo lịch. Mốc quan trọng nhất là hết **giai đoạn 1**: từ đó giao được cho trung tâm đầu tiên; các giai đoạn sau làm song song với việc chạy thật và sắp lại thứ tự theo phản hồi của trung tâm đó.
