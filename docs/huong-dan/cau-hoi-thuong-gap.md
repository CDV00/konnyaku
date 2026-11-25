# Câu hỏi thường gặp và xử lý sự cố

[← Mục lục](README.md)

## Đăng nhập

| Tình huống | Cách xử lý |
| --- | --- |
| "Thông tin đăng nhập không đúng." | Kiểm tra đúng tab: học viên, phụ huynh dùng **Học viên · Phụ huynh** với tên đăng nhập `hv…`; giáo viên, nhân viên dùng **Giáo viên · Nhân viên** với email. Kiểm tra phím Caps Lock |
| "Tài khoản tạm khóa do nhập sai nhiều lần. Thử lại sau 15 phút." | Chờ 15 phút. Quên mật khẩu thì xem dòng dưới |
| Quên mật khẩu (nhân viên, giáo viên) | **Quên mật khẩu? Nhận link đặt lại qua email** ở tab Giáo viên · Nhân viên ([hướng dẫn](dang-nhap-va-bao-mat.md#quên-mật-khẩu)) |
| Quên mật khẩu (học viên, phụ huynh) | Liên hệ trung tâm; quản trị **Bảo mật** → **Đặt lại mật khẩu** để cấp mật khẩu tạm mới |
| Không nhận được email đặt lại mật khẩu | Xem cả thư rác / quảng cáo; đợi vài phút. Mỗi giờ tối đa 3 link. Vẫn không có: nhờ quản trị **Nhân sự** → **Đặt lại mật khẩu** |
| "Link đặt lại đã hết hạn hoặc đã được dùng." | Link chỉ dùng 30 phút, một lần. Bấm **Yêu cầu link mới** |
| "Tài khoản đã bị khoá. Liên hệ quản trị trung tâm." | Quản trị đã khoá tài khoản; nhờ quản trị **Mở khoá** (học viên: **Bảo mật**) hoặc **Dùng lại** (nhân sự: **Nhân sự**) |
| "Mã không đúng hoặc đã dùng." (2FA) | Mã đổi mỗi 30 giây và mỗi mã dùng một lần; nhập mã mới nhất. Kiểm tra giờ trên điện thoại đặt tự động |
| Mất điện thoại có ứng dụng xác thực | **Mất điện thoại? Dùng mã khôi phục**; hết mã thì nhờ quản trị **Gỡ xác thực 2 bước** |
| "Phiên đăng nhập đã kết thúc…" | Bạn vừa đổi mật khẩu, đăng xuất ở nơi khác, bị khoá hoặc đổi vai trò. Đăng nhập lại |
| "Phiên đăng nhập đã hết, vui lòng đăng nhập lại." | Đăng nhập lại rồi làm lại thao tác |
| "Không xác định được trung tâm của địa chỉ này." / trang **Không tìm thấy trung tâm** | Sai địa chỉ: mỗi trung tâm có địa chỉ riêng; dùng đúng link trung tâm gửi |
| "Trung tâm đang tạm ngừng hoạt động trên Konnyaku…" | Trung tâm bị tạm khoá; quản trị trung tâm liên hệ Konnyaku |
| Nhân viên đăng nhập xong vào thẳng `/teach` thay vì `/ops` | Tài khoản chỉ có vai trò Giáo viên. Cần thêm vai trò thì nhờ quản trị |

## Vào lớp và phòng học

| Tình huống | Cách xử lý |
| --- | --- |
| Không thấy nút **Vào lớp** | Buổi học tại cơ sở (không có phòng online); hoặc phòng chưa được tạo; hoặc học viên chưa vào lớp (đơn chưa trả đủ). Gần giờ học vẫn không có: báo giáo vụ |
| "Không tìm thấy phòng học, hoặc bạn không có chỗ trong lớp này." | Đăng nhập đúng tài khoản; học viên chưa có chỗ trong lớp thì liên hệ trung tâm |
| "Phòng đã đủ người." | Phòng học có sẵn tối đa 6 người gồm giáo viên. Báo giáo viên / trung tâm |
| Không có hình / tiếng của mình | Cho phép camera / micro: biểu tượng ổ khoá cạnh địa chỉ trang → Cho phép → tải lại trang. Đóng ứng dụng khác đang dùng camera |
| Không nghe thấy người khác | Kiểm tra loa / tai nghe, âm lượng máy; người kia có đang tắt micro (biểu tượng micro gạch chéo) |
| Ô của người khác cứ **Đang kết nối…** | Mạng chặn kết nối trực tiếp; thử mạng khác (4G). Cả lớp bị: báo quản trị kỹ thuật kiểm tra máy chủ TURN |
| "Bạn vừa mở phòng này ở một tab hoặc thiết bị khác…" | Mỗi người chỉ ở một nơi; đóng tab / máy kia |
| Hình giật, chậm | Tắt camera khi không nói; đóng ứng dụng nặng mạng; lớp đông thì nên dùng Google Meet |
| Không xem được video buổi học | Phòng học có sẵn của Konnyaku chưa ghi hình; chỉ buổi học trên Google Meet có ghi hình mới có video |

## Học phí, số buổi

| Tình huống | Cách xử lý |
| --- | --- |
| Đã thanh toán nhưng học viên chưa vào lớp | Đợi vài giây rồi tải lại trang. Đơn vẫn **Chờ thanh toán**: chưa trả đủ (xem **Đã thu**). Đơn **Cần hoàn tiền**: lớp đã đầy; hoàn tiền hoặc tạo đơn cho lớp khác |
| Số buổi của học viên không giảm sau buổi học | Giáo viên chưa **Xác nhận điểm danh**, hoặc học viên được ghi **Vắng có phép** |
| Học viên bị trừ buổi dù vắng | Vắng không phép vẫn bị trừ buổi (quy tắc). Nếu học viên đã báo nghỉ: kế toán **Điều chỉnh buổi** cộng lại 1 buổi, ghi lý do |
| Xác nhận điểm danh nhầm | Không sửa trên giao diện; kế toán **Điều chỉnh buổi** trong sổ buổi học |
| Học viên "Đang nợ … buổi" | Đã học quá số buổi đã mua. Tạo đơn gói mới, thu tiền |
| Nhập nhầm số tiền thanh toán | **Chi tiết** đơn → **Huỷ thanh toán** khoản nhầm (phiếu giữ lại với dấu ĐÃ HỦY), ghi nhận lại khoản đúng |
| Muốn giảm giá cho một học viên | Khi đơn còn **Chờ thanh toán**: **Chi tiết** → **Sửa đơn hàng** → giảm **Số tiền** |
| Hoàn nhầm | Không hoàn tác được. Liên hệ Konnyaku |
| "Đã in/tải phiếu này 20 lần trong 24 giờ." | Giới hạn chống in tràn lan; đợi hôm sau hoặc nhờ người khác có quyền |
| Kế toán cơ sở không thấy nút **Ghi nhận** | Hạn chế của phiên bản này; nhờ kế toán toàn trung tâm hoặc quản trị ghi nhận |

## Lớp, lịch

| Tình huống | Cách xử lý |
| --- | --- |
| "Giáo viên hoặc phòng bị trùng lịch." | Giáo viên / phòng đã có buổi khác cùng giờ. Đổi giờ, ngày, giáo viên hoặc phòng |
| "Không đủ ngày để xếp hết các buổi." | Lịch quá dài (quá nhiều ngày nghỉ hoặc quá ít ngày học mỗi tuần); thêm ngày học trong tuần |
| "Ngày áp dụng không được ở quá khứ." | **Áp dụng từ** phải từ hôm nay trở đi |
| "Lớp đã xếp lịch: đổi ngày bằng “Sửa lịch tuần”." | Ngày khai giảng khoá khi đã có lịch |
| Thêm ngày nghỉ nhưng buổi học vẫn rơi vào ngày đó | Ngày nghỉ không tự dời buổi đã xếp. **Sửa lịch tuần** áp dụng từ trước ngày nghỉ |
| Muốn đổi giờ / giáo viên một buổi | Chưa có; xem [Đổi lịch, đổi giáo viên](quy-trinh.md#4-đổi-lịch-đổi-giáo-viên) |
| Học viên không thấy nút **Xin nghỉ** | Đã quá hạn xin nghỉ (mặc định 12 giờ trước buổi học), buổi là buổi học bù / lớp linh hoạt (dùng **Huỷ đặt**), hoặc đã xin nghỉ đủ số lần của khoá |
| "Bạn đã xin nghỉ đủ … lần cho khoá này." | Đã dùng hết số lần xin nghỉ; nếu trung tâm đồng ý, giáo viên chọn **Vắng có phép** khi điểm danh |
| **Chọn buổi học bù** trống | Không có lớp khác cùng cấp độ dạy bài đó còn chỗ trong hạn; bấm **Nhờ giáo vụ xếp** |
| "Buổi này đã hết chỗ. Bạn có thể vào hàng chờ." | Bấm **Vào hàng chờ**; khi có người huỷ, chỗ được giữ 2 giờ ở đầu trang **Lịch học** |
| Không thấy nút **Chuyển sang Tại cơ sở** | Phòng đã đủ chỗ, hoặc còn dưới 2 giờ trước buổi học |
| Học viên đã xin nghỉ không có trong trang điểm danh | Đúng: buổi đã xin nghỉ không điểm danh, không trừ buổi |
| Muốn thêm học viên vào lớp không qua đơn hàng (học thử, học bổng) | Chưa có; học viên chỉ vào lớp khi đơn gắn lớp được trả đủ |

## Quyền và dữ liệu

| Tình huống | Cách xử lý |
| --- | --- |
| "Bạn không có quyền thực hiện thao tác này." | Vai trò của bạn không làm việc đó ([bảng quyền](quy-tac-nghiep-vu.md#phân-quyền-theo-vai-trò-và-cơ-sở)); nhờ quản trị |
| "Bạn không phụ trách cơ sở này." | Bạn được giới hạn theo cơ sở; nhờ người phụ trách cơ sở đó hoặc quản trị |
| Trang **Kỳ lương** / **Nhân sự** trống dù trung tâm có dữ liệu | Bạn không có quyền xem mục đó |
| Không thấy nút **Hiện** liên hệ | Vai trò của bạn (kế toán, giáo viên) chỉ xem dạng che |
| "Bạn đã xuất/in 20 lần trong 24 giờ qua." | Giới hạn chống lộ dữ liệu; liên hệ quản trị |
| "Danh sách có … dòng, vượt giới hạn 2000 dòng…" | Lọc bớt (theo cơ sở, ngày tạo) rồi xuất từng phần |

## Email

| Tình huống | Cách xử lý |
| --- | --- |
| Học viên không nhận email nào | Hồ sơ chưa có email, hoặc email sai. Quản trị xem **Thông báo** → lọc **Lỗi** để biết lý do. Học viên có thể đã tắt email loại đó trong **Cài đặt nhận thông báo** (thông báo vẫn có trong chuông) |
| Chuông không tự cập nhật | Mạng chặn kết nối trực tiếp: chuông vẫn tự cập nhật mỗi 30 giây. Tải lại trang nếu cần ngay ([Thông báo](thong-bao.md)) |
| Không nhận được thông báo đẩy | Chưa bật trên thiết bị đó, trình duyệt đang chặn thông báo, hoặc đã đăng xuất (đăng xuất tự tắt). Mở **Cài đặt nhận thông báo** → **Bật trên thiết bị này** ([Thông báo](thong-bao.md#thông-báo-đẩy-trên-thiết-bị-này)) |
| Email bị lỗi vĩnh viễn | Sửa email trong hồ sơ; khi lỗi phía người nhận đã khắc phục, bấm **Gửi lại** |
| Email đến bằng tiếng Anh | **Ngôn ngữ liên lạc** của học viên là Tiếng Anh; **Sửa** hồ sơ để đổi |

## Báo lỗi cho Konnyaku

Gửi kèm: địa chỉ trang, việc đang làm, thông báo lỗi (chụp màn hình), thời điểm, và **Mã tham chiếu** nếu trang **Đã có lỗi xảy ra** hiện mã này.
