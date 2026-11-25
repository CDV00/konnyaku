# Xử lý dữ liệu cá nhân

Cách Konnyaku giữ dữ liệu cá nhân của học viên (nhiều em là trẻ em), phụ huynh và khách đăng ký (lead) theo Nghị định 13/2023/NĐ-CP và Luật Bảo vệ dữ liệu cá nhân (hiệu lực 1/1/2026). Trang chính sách quyền riêng tư cho khách nằm ở trang công khai; tài liệu này mô tả phần xử lý trong hệ thống, cho người vận hành và người phát triển.

## Dữ liệu nào là dữ liệu cá nhân

| Nơi | Trường | Mức |
| --- | --- | --- |
| Crm — hồ sơ học viên, lead | Họ tên, năm sinh, tên phụ huynh/người giám hộ, điện thoại, email; với lead thêm mục tiêu, lịch mong muốn (chữ tự do) | Dữ liệu cá nhân cơ bản (trẻ em: cần đồng ý của người giám hộ) |
| Identity — tài khoản | Tên hiển thị, email, tên đăng nhập | Cơ bản |
| Workspace — vào phòng học | Tên hiển thị lúc vào phòng | Cơ bản |
| Notification — thư đã gửi | Địa chỉ, tên người nhận, dữ liệu mẫu (có thể có tên học viên/lead) | Cơ bản |
| Billing, Academic, Learning | Chỉ giữ `studentId` (không tên, không liên hệ); mã giao dịch thanh toán do nhân viên nhập | Gián tiếp |

Không lưu số CCCD, tài khoản ngân hàng hay dữ liệu sức khoẻ. Thuộc tính `[PersonalData]` (SharedKernel) đánh dấu các trường trên trong code; bộ che dùng chung là `PersonalDataMask`.

## Che theo vai trò

Danh sách luôn trả liên hệ đã che, kể cả cho quản trị viên: `0901234567` → `09xx xxx 567`, `an.nguyen@example.com` → `a***@example.com`, năm sinh `2014` → `201*`, tên phụ huynh `Nguyễn Văn An` → `Nguyễn V** A**`. Họ tên học viên/lead vẫn hiện đầy đủ (cần để làm việc).

Giá trị đầy đủ chỉ lấy được từng bản ghi một qua `POST /api/crm/{students|leads}/{id}/reveal` (nút **Hiện**, hoặc khi mở form **Sửa**) và mỗi lần đều ghi nhật ký.

| Vai trò | Danh sách học viên | Hiện liên hệ học viên | Danh sách lead + hiện liên hệ | Xuất CSV / In | Yêu cầu dữ liệu, nhật ký |
| --- | --- | --- | --- | --- | --- |
| Quản trị (TenantAdmin) | ✓ (che) | ✓ | ✓ | ✓ | ✓ |
| Quản lý cơ sở | ✓ (che) | ✓ | lead của cơ sở mình | chỉ dữ liệu cơ sở mình | — |
| Điều phối | ✓ (che) | ✓ | — | — | — |
| Tư vấn | ✓ (che) | ✓ | ✓ | — | — |
| Kế toán | ✓ (che) | — | — | — | — |
| Giáo viên | — (chỉ tên học viên trong lớp mình dạy, ở `teach`) | — | — | — | — |

Quyền nằm trong ma trận `RolePermissions` (`backend/src/BuildingBlocks/Konnyaku.Application/Authorization/Permissions.cs`): hiện liên hệ học viên cần `crm.students`, lead cần `crm.leads` (và đúng cơ sở), xuất/in cần `crm.export`, yêu cầu dữ liệu và nhật ký cần `crm.privacy`.

Lịch sử thay đổi (`change_log`) ghi các trường dữ liệu cá nhân ở dạng đã che: thấy được "số điện thoại đã đổi", không thấy số.

## Xuất và in danh sách

- Chỉ quản trị và quản lý cơ sở; quản lý cơ sở chỉ xuất học viên/lead của cơ sở mình.
- Kiểm tra ở API (`GET /api/crm/{students|leads}/export`, `/print`), không ở giao diện: tối đa `Privacy:ExportMaxRows` dòng mỗi lần (mặc định 2.000, nhiều hơn phải lọc bớt) và `Privacy:ExportsPerDay` lần xuất + in mỗi người trong 24 giờ (mặc định 20).
- File CSV (UTF-8 có BOM, chống CSV injection) có dòng cuối "Xuất bởi <tên đã che> lúc <giờ máy chủ> · N dòng"; bản in có cùng dấu ở chân mỗi trang và liên hệ đã che.
- Mỗi lần xuất/in ghi nhật ký: ai, lúc nào, bộ lọc (từ khoá tìm kiếm lưu dạng che), số dòng, IP.

## Nhật ký truy cập dữ liệu cá nhân

Bảng `crm.personal_data_access`: RLS theo trung tâm, **chỉ thêm** (role `kk_crm` không có quyền UPDATE/DELETE). Ghi các hành động: xem đầy đủ (kèm mục đích: xem / sửa / liên hệ), xuất CSV, in, xuất dữ liệu cho chủ thể, ẩn danh hoá, ẩn danh hoá tự động. Không ghi giá trị dữ liệu cá nhân. Quản trị xem ở `/ops/privacy` → **Nhật ký truy cập** (lọc theo hành động, khoảng ngày).

IP là địa chỉ máy gọi API; khi web và API cùng mạng nội bộ đó là IP của web server cho đến khi web chuyển tiếp `X-Forwarded-For`.

## Yêu cầu của chủ thể dữ liệu

Quản trị ghi nhận ở `/ops/privacy` → **Ghi nhận yêu cầu** (học viên hoặc lead, loại yêu cầu, ghi chú). Hạn xử lý = lúc ghi + `Privacy:RequestDueHours` (mặc định 72 giờ); yêu cầu quá hạn hiện đỏ.

| Loại | Cách làm |
| --- | --- |
| Xem / nhận bản sao | **Xuất dữ liệu**: file JSON gồm mọi module (`GET /api/crm/privacy/{students|leads}/{id}/export`) — hồ sơ, lead gốc, tài khoản, ghi danh, điểm danh, đơn hàng, thanh toán, sổ buổi học. Mỗi module tự đọc dữ liệu của mình qua `IPersonalDataSource` (không đọc chéo schema). Gửi file cho người yêu cầu qua kênh an toàn rồi xoá bản tải về. |
| Sửa | Sửa hồ sơ/lead như bình thường, rồi **Đóng yêu cầu** ghi đã sửa gì. |
| Xoá | **Ẩn danh hoá** (xem dưới). Học viên còn đang học phải **Ngừng dùng** trước. |
| Rút đồng ý | Lead: ẩn danh hoá. Học viên đang học: dữ liệu cần cho hợp đồng học vẫn giữ tới khi kết thúc; ghi rõ trong kết quả và ẩn danh hoá khi học viên nghỉ. |

Đóng yêu cầu: **Đã xong** hoặc **Từ chối** (bắt buộc ghi lý do).

### Ẩn danh hoá giữ sổ buổi học và chứng từ

Xoá = ghi đè dữ liệu cá nhân, không xoá dòng:

1. Crm: họ tên thành `#<8 ký tự cuối của id>`, năm sinh, phụ huynh, điện thoại, email bị xoá; lead gốc cũng vậy (thêm mục tiêu, lịch mong muốn, UTM). Cột tìm kiếm sinh tự động nên cũng hết dữ liệu.
2. Giá trị cũ/mới trong lịch sử thay đổi của bản ghi bị xoá bằng hàm `crm.redact_change_log` / `identity.redact_change_log` (SECURITY DEFINER, chỉ xoá giá trị, chỉ trong trung tâm hiện tại; bảng vẫn chỉ-thêm với role của module).
3. Event `StudentAnonymizedV1(studentId, userId)`: Identity xoá tên/email tài khoản học viên, xoá tên đăng nhập và khoá thành viên (người đồng thời là nhân viên thì chỉ mất tài khoản học viên); Workspace thay tên trong lịch sử vào phòng; Notification xoá địa chỉ, tên, dữ liệu mẫu và tiêu đề của các thư gửi tới tài khoản đó (thư chưa gửi bị bỏ).
4. **Không đụng** sổ buổi học (`billing.credit_ledger_entry`, chỉ-thêm), đơn hàng, thanh toán, ghi danh, điểm danh: các dòng này chỉ có `studentId`, vẫn đúng cho kế toán và đối soát (Luật Kế toán yêu cầu lưu chứng từ), nhưng không còn chỉ tới ai.
5. Ghi nhật ký "Ẩn danh hoá" và tự đóng yêu cầu đi kèm.

Không hoàn tác được. Hồ sơ đã ẩn danh không sửa, không kích hoạt lại được.

## Lưu giữ

- Lead không chuyển thành học viên được giữ `Privacy:LeadRetentionMonths` tháng kể từ lần thay đổi cuối (mặc định 24; đặt `0` để tắt), sau đó Worker (`LeadRetentionService`, chạy mỗi ngày, từng trung tâm trong ngữ cảnh RLS của nó) ẩn danh hoá như trên và ghi nhật ký "Ẩn danh hoá tự động" (người thực hiện: Hệ thống). Dòng lead còn lại cho thống kê (ngôn ngữ, cơ sở, nguồn, ngày).
- Lead đã chuyển đổi đi theo hồ sơ học viên (ẩn danh khi học viên yêu cầu xoá).
- Nhật ký truy cập và lịch sử thay đổi giữ cùng thời gian với dữ liệu trung tâm.

Cấu hình (biến môi trường của API và Worker): `Privacy__ExportMaxRows`, `Privacy__ExportsPerDay`, `Privacy__LeadRetentionMonths`, `Privacy__RequestDueHours`.

## Log ứng dụng

- API: Serilog, `Microsoft.AspNetCore` và `Microsoft.EntityFrameworkCore` ở mức Warning: không log từng request (URL có từ khoá tìm kiếm), không log tham số SQL (`EnableSensitiveDataLogging` không bật, lỗi DB hiện `@p0='?'`). Chuỗi kết nối không bật `Include Error Detail`, nên lỗi trùng khoá không chép giá trị vào log.
- Worker: chỉ log lỗi outbox (id, loại event) và số lead đã ẩn danh theo trung tâm.
- Caddy (production) không bật access log.
- Khi thêm log: ghi id hoặc giá trị đã che (`PersonalDataMask`), không ghi tên, điện thoại, email.

## Tìm kiếm và phân trang

Danh sách tìm theo cột `search_text` sinh tự động (`public.kk_unaccent(lower(tên || sđt || email || phụ huynh))`, index GIN `pg_trgm`), nên "an" và "Ân" ra cùng kết quả; số điện thoại gõ kiểu `+84 90 123` được đổi về `090123`. Extension `unaccent` và hàm `kk_unaccent` có trong script init (dev, prod) và migration Crm tạo lại nếu thiếu. Từ khoá tìm kiếm nằm trên URL để chia sẻ được link đã lọc; không có log nào ghi URL này (xem trên).
