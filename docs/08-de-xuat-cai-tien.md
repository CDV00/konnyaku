# 8. Đề xuất cải tiến (11/2026)

Danh sách việc nên làm tiếp, rút ra khi rà code, chạy hệ thống với dữ liệu review (`infra/scripts/seed_review.py`) và đối chiếu với cột "còn để sau" trong `README.md`. Đây là **đề xuất để chọn**, chưa phải phạm vi đã chốt: việc nào được chọn thì đưa vào [`04-roadmap.md`](04-roadmap.md) (nguồn duy nhất về phạm vi).

Ước lượng công: **S** ≤ 2 ngày, **M** ≤ 1 tuần, **L** > 1 tuần (một người, đã quen code).

Đang làm song song (không lặp lại ở dưới): sửa mọi bản ghi trong `/ops` + phân quyền theo vai trò/cơ sở + lịch sử thay đổi; phòng học WebRTC P2P thay Google Meet; đo và tối ưu RAM.

## A. Nhanh, giá trị cao

| # | Việc | Vì sao (bằng chứng) | Công |
|---|---|---|---|
| A1 | **Liên kết phụ huynh với học viên** | Vai trò `Parent` đã có và đăng nhập được, nhưng không gắn với học viên nào nên `/learn` của phụ huynh trống. Phụ huynh là người trả tiền, cần thấy lịch, số buổi còn lại, điểm danh của con (một phụ huynh nhiều con) | M |
| A2 | **Thông báo tự động** (email trước, Zalo ZNS sau) | Các event đã phát nhưng chưa ai nghe: `CreditLowV1` (sắp hết buổi), `LeadCreatedV1` (lead mới cho tư vấn viên), `LearnerAccountCreatedV1`, `EnrollmentDroppedV1`, `PayrollPeriodLockedV1`. Roadmap V1 có "email nhắc lịch T-24h". Chỉ cần thêm module `Notification` tiêu thụ event + template theo `(key, kênh, locale)` | M |
| A3 | **Tìm kiếm, lọc, phân trang** ở danh sách `/ops` | Danh sách lớp, học viên, lead, đơn hàng đang cắt cứng ở 500 dòng (`Take(500)`), không có tìm kiếm: trung tâm thứ 501 học viên sẽ "mất" học viên trên màn hình mà không báo | M |
| A4 | **Trang chủ `/ops` theo vai trò** | Tư vấn viên, kế toán mở `/ops` thấy thẻ thống kê bằng 0 vì API (đúng) không cho đọc dữ liệu ngoài quyền. Mỗi vai trò nên có bảng điều khiển riêng: tư vấn (lead cần gọi hôm nay), kế toán (đơn chờ thu, hoàn tiền), điều phối (buổi hôm nay, buổi thiếu giáo viên/phòng) | S |
| A5 | **Báo cáo doanh thu trong `/ops`** | API `GET /api/billing/reports/recognized-revenue` đã có nhưng chưa có màn hình nào dùng | S |
| A6 | **Nhập học viên hàng loạt từ CSV** | Trung tâm chuyển từ Excel sang sẽ có vài trăm học viên; hiện chỉ thêm từng người | S |

## B. Tính năng lớn phục vụ người dùng

| # | Việc | Người hưởng lợi | Công |
|---|---|---|---|
| B1 | **Học bù và cảnh báo nghỉ học**: học viên vắng có phép được xếp buổi bù ở lớp cùng cấp độ; vắng 2 buổi liên tiếp thì báo tư vấn viên/phụ huynh | Học viên, phụ huynh, điều phối | M |
| B2 | **Thanh toán online** (VNPay/MoMo/chuyển khoản QR) và **hóa đơn điện tử** | Kế toán, phụ huynh | L |
| B3 | **Lịch rảnh của giáo viên, dạy thay**: xếp lịch gợi ý giáo viên rảnh; giáo viên báo nghỉ thì điều phối chọn người dạy thay, bảng công tự tính đúng người | Điều phối, giáo viên | M |
| B4 | **Đặt lịch test đầu vào / học thử** từ trang công khai, kết quả test gợi ý cấp độ | Tư vấn, khách mới | M |
| B5 | **Nhận xét sau buổi học và sổ liên lạc**: giáo viên ghi nhận xét, giao bài; phụ huynh xem trong `/learn` | Giáo viên, phụ huynh | M |
| B6 | **Ghi hình và lưu trữ video** cho phòng học WebRTC (object storage + HLS, có watermark) | Học viên | L |
| B7 | **PWA + thông báo đẩy** cho `/learn` và `/teach` (cài như app trên điện thoại, nhắc giờ học) | Học viên, giáo viên | M |
| B8 | **Đăng nhập Google, 2FA cho nhân viên** (đã ghi "còn để sau" ở Phase 1) | Mọi người, bảo mật | M |

## C. Tối ưu kỹ thuật và vận hành

| # | Việc | Vì sao | Công |
|---|---|---|---|
| C1 | **Giới hạn tần suất dùng Redis** | Bộ đếm của form đăng ký đang nằm trong RAM từng bản sao API; 2 bản sao = hạn mức gấp đôi | S |
| C2 | **Deploy không gián đoạn** (cuốn chiếu từng bản sao) | `docker compose up -d` tạo lại mọi bản sao cùng lúc, mất vài giây mỗi lần deploy | S |
| C3 | **Theo dõi lỗi và hiệu năng**: OpenTelemetry (trace web → API → DB), Sentry, Uptime Kuma | Hiện chưa có tracing hay cảnh báo lỗi nào; chỉ có log container | M |
| C4 | **Bảo vệ dữ liệu cá nhân** theo Nghị định 13 và Luật BVDLCN: che bớt SĐT/email khi hiển thị cho vai trò không cần, ghi nhật ký khi xuất/in danh sách học viên, chính sách lưu trữ và xóa dữ liệu theo yêu cầu | Dữ liệu trẻ em và phụ huynh; bắt buộc trước khi có khách thật | M |
| C5 | **Backup khôi phục theo thời điểm** (pgBackRest hoặc managed PostgreSQL) | `backup.sh` hiện chỉ cho điểm khôi phục theo ngày; sổ buổi học là dữ liệu tiền | S |
| C6 | **Test giao diện tự động** (Playwright) cho các luồng chính: đăng nhập 2 realm, ghi danh sau thanh toán, điểm danh → trừ buổi | Hiện chỉ có test backend; lỗi kiểu "đăng xuất mất host tenant" chỉ phát hiện khi bấm tay | M |
| C7 | **Nâng các GitHub Action** lên bản chạy Node 24 (`checkout`, `docker/*`) | GitHub đã cảnh báo Node 20 ngừng hỗ trợ | S |

## Thứ tự gợi ý

1. Trước khi có trung tâm thật: **C4** (dữ liệu cá nhân), **C5** (backup), **A3** (danh sách >500), **C3** (biết khi nào hỏng).
2. Đợt bán đầu tiên: **A1** (phụ huynh), **A2** (thông báo), **A4**, **A5**, **A6**.
3. Giữ chân khách: **B1**, **B3**, **B5**, rồi **B2** khi số giao dịch đủ lớn.
