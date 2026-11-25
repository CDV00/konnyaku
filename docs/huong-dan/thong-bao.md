# Thông báo: chuông, hộp thông báo và cài đặt nhận

Dùng chung cho học viên (`/learn`), giáo viên (`/teach`) và nhân viên (`/ops`).

Mỗi khi có việc liên quan đến bạn (buổi học đổi giờ, có bài mới, có điểm…), Konnyaku ghi một **thông báo** vào hộp thông báo của tài khoản bạn. Thông báo hiện ngay trên trang đang mở, **không cần tải lại trang**. Ngoài ra bạn có thể nhận thêm qua **email** và **thông báo đẩy** (hiện trên máy tính, điện thoại kể cả khi đã đóng trang).

## Chuông thông báo

Biểu tượng chuông nằm ở thanh trên cùng (ở `/ops`: góc trên bên phải, trên điện thoại cạnh nút giao diện sáng/tối). Số màu đỏ là số thông báo **chưa đọc**.

1. Bấm chuông để xem các thông báo mới nhất. Thông báo chưa đọc có chấm xanh và chữ đậm.
2. Bấm một thông báo để mở đúng trang liên quan (lịch học, bài tập, kết quả…). Thông báo đó được đánh dấu **đã đọc**.
3. **Đánh dấu đã đọc hết**: xoá số trên chuông.
4. **Xem tất cả**: mở trang **Thông báo** (`/learn/inbox`, `/teach/inbox`, `/ops/inbox`), lọc **Tất cả** / **Chưa đọc**, bấm **Xem thêm** để xem thông báo cũ hơn.
5. Biểu tượng bánh răng: mở **Cài đặt nhận thông báo**.

Đọc ở một tab thì các tab khác đang mở cũng cập nhật theo. Khi mạng chập chờn, chuông tự kết nối lại và lấy đủ thông báo đã lỡ; trong lúc mất kết nối, chuông vẫn tự cập nhật mỗi 30 giây.

## Những thông báo bạn nhận

| Loại | Khi nào | Ai nhận | Email |
| --- | --- | --- | --- |
| Buổi học đổi giờ | Một buổi bị đổi giờ | Học viên của lớp, giáo viên của buổi | Có |
| Buổi học bị hủy | Một buổi bị hủy | Học viên của lớp, giáo viên của buổi | Có |
| Nhắc lịch | 24 giờ trước mỗi buổi | Học viên của lớp, giáo viên của buổi | Có |
| Có chỗ trống từ danh sách chờ | Một chỗ được giữ cho bạn | Học viên đang chờ | Không |
| Yêu cầu học bù được duyệt | Giáo vụ xếp buổi học bù | Học viên | Không |
| Bài mới | Bài tập, bài kiểm tra được giao (lúc bài mở) | Học viên được giao | Không |
| Sắp hết hạn nộp | 24 giờ trước hạn nộp | Học viên **chưa nộp** | Không |
| Có điểm | Điểm được công bố cho bạn | Học viên | Không |
| Ghi danh vào lớp | Học viên vào lớp | Học viên | Có |
| Gói học sắp hết buổi | Còn ≤ 3 buổi | Học viên | Có |
| Khách hàng mới | Có lead mới | Tư vấn viên được giao (chưa giao: quản trị) | Có |
| Chốt bảng lương | Kỳ lương được chốt | Giáo viên | Có |

Học viên chưa có tài khoản không có hộp thông báo; họ chỉ nhận email (nếu hồ sơ có email).

## Cài đặt nhận thông báo

Mở bằng biểu tượng bánh răng trên chuông, hoặc nút **Cài đặt nhận thông báo** trên trang **Thông báo**.

1. Mỗi loại thông báo có ba kênh: **Trong ứng dụng** (luôn bật, không tắt được), **Email**, **Thông báo đẩy**. Loại không có email ghi **Không có email**.
2. Gạt công tắc để bật / tắt từng kênh, rồi bấm **Lưu cài đặt**. Hiện **Đã lưu.** là xong.
3. Tắt email một loại thì thông báo loại đó vẫn vào hộp thông báo, chỉ không gửi email nữa.

**Gom email**: nhiều thông báo cùng loại trong 10 phút (ví dụ giáo vụ hủy liền 3 buổi) được gộp thành **một** email liệt kê từng thông báo, để hộp thư không bị đầy.

### Thông báo đẩy trên thiết bị này

1. Trong mục **Thông báo đẩy trên thiết bị này**, bấm **Bật trên thiết bị này**.
2. Trình duyệt hỏi quyền hiện thông báo: chọn **Cho phép**.
3. Hiện **Đang bật trên thiết bị này.** là xong. Mỗi trình duyệt, mỗi máy phải bật riêng.

- Bấm **Tắt trên thiết bị này** để thôi nhận trên máy đó. **Đăng xuất** cũng tự tắt thông báo đẩy trên máy đó.
- Thông báo đẩy chỉ hiện tiêu đề và một dòng ngắn; bấm vào để mở trang liên quan.
- Đã chặn thông báo trong trình duyệt: trang ghi "Trình duyệt đang chặn thông báo…", mở cài đặt trang web của trình duyệt (biểu tượng ổ khoá cạnh địa chỉ) để cho phép.
- iPhone / iPad: thêm trang vào **Màn hình chính** (Chia sẻ → Thêm vào MH chính) rồi mở từ đó thì mới bật được.
- Trung tâm chưa cấu hình thông báo đẩy: trang ghi "Trung tâm chưa bật thông báo đẩy." (hộp thông báo và email vẫn hoạt động).
