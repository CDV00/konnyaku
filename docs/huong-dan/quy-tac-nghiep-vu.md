# Quy tắc nghiệp vụ cần biết

[← Mục lục](README.md)

Các quy tắc hệ thống tự áp dụng. Ở phiên bản này các con số dưới đây cố định cho mọi trung tâm, **chưa chỉnh được trên giao diện**.

## Điểm danh và trừ buổi

### Đề xuất trạng thái cho lớp online

Sau buổi học online, hệ thống đề xuất trạng thái từ thời gian mỗi học viên có mặt trong phòng (so với thời lượng buổi):

| Điều kiện (xét lần lượt từ trên xuống) | Trạng thái đề xuất |
| --- | --- |
| Không vào phòng, hoặc có mặt **dưới 30%** thời lượng | **Vắng** |
| Vào phòng muộn **hơn 10 phút** so với giờ bắt đầu | **Đi muộn** |
| Có mặt **từ 70%** thời lượng trở lên | **Có mặt** |
| Có mặt từ 30% đến dưới 70% | **Về sớm** |

Học viên **học bù** có mặt (Có mặt, Đi muộn hay Về sớm) được ghi **Học bù**. Học viên đã xin nghỉ hoặc đang bảo lưu không có trong danh sách điểm danh của buổi.

Ví dụ buổi 90 phút: vào đúng giờ, ở 90 phút → Có mặt; vào phút thứ 18, ở 72 phút → Đi muộn; ở 20 phút → Vắng.

Giáo viên sửa được mọi đề xuất trước khi bấm **Xác nhận điểm danh**. Lớp tại cơ sở không có đề xuất (xem [Giáo viên → Điểm danh](giao-vien.md#điểm-danh)).

### Buổi nào bị trừ

Khi giáo viên xác nhận điểm danh, mỗi học viên bị trừ **1 buổi** cho mọi trạng thái **trừ Vắng có phép**:

| Trạng thái | Trừ buổi? |
| --- | --- |
| Có mặt, Đi muộn, Về sớm | Có |
| **Học bù** | Có, trừ vào gói của lớp gốc của học viên |
| **Vắng** (không phép) | **Có** |
| **Vắng có phép** | Không |
| Đã **xin nghỉ** đúng hạn (không có trong danh sách) | Không |

Muốn học viên báo nghỉ không bị trừ: học viên tự **Xin nghỉ** trên cổng học viên trước hạn (xem bên dưới), hoặc giáo viên chọn **Vắng có phép** cho học viên đó trước khi xác nhận.

### Thứ tự trừ: buổi trả phí trước, buổi tặng sau

Mỗi đơn có hai loại buổi: **trả phí** (số buổi của gói) và **tặng**. Hệ thống trừ buổi **trả phí trước**; chỉ khi hết buổi trả phí mới trừ buổi tặng. Buổi trả phí đã học được tính là doanh thu ghi nhận của cơ sở trong ngày học.

### Nợ buổi

Khi hết cả buổi trả phí lẫn buổi tặng mà học viên vẫn học, số buổi trả phí bị **âm** (nợ). Học viên vẫn vào lớp được; cổng học viên hiện dòng đỏ **Đang nợ … buổi — vui lòng gia hạn gói học.** Trung tâm thu tiền gói mới để bù.

Khi số buổi còn lại của một đơn **từ 3 trở xuống**, học viên nhận email "Sắp hết buổi" (nếu hồ sơ có email).

### Sổ buổi học chỉ ghi thêm

Mọi thay đổi số buổi (mua, tặng, học, hoàn, điều chỉnh) là một **dòng mới** trong sổ buổi học; không dòng nào bị sửa hay xoá. Điểm danh nhầm hay cần tặng thêm thì kế toán ghi **Điều chỉnh buổi** (xem [Kế toán → Sổ buổi học](ke-toan.md#sổ-buổi-học)).

## Xin nghỉ, học bù và chỗ ngồi

Mỗi buổi có **chỗ ngồi** riêng: danh sách học viên của buổi lấy từ chỗ đã đặt, không phải từ danh sách lớp.

| Quy tắc | Mặc định |
| --- | --- |
| Số chỗ online của buổi | Sĩ số tối đa + chỗ học bù |
| Số chỗ tại cơ sở | Như trên nhưng **không vượt sức chứa phòng**; lớp kết hợp có cả hai loại chỗ |
| Học viên lớp kết hợp | Ngồi tại cơ sở khi phòng còn chỗ, hết chỗ thì học online (trừ khi đã chọn online) |
| Xin nghỉ | Trước giờ học **12 giờ**, tối đa **3 lần** mỗi khoá; buổi không bị trừ, nhận một quyền học bù |
| Quyền học bù | Dùng trong **30 ngày**, ở buổi **cùng bài** của lớp khác cùng cấp độ, khi buổi còn **chỗ học bù** (mặc định 2 chỗ mỗi buổi); không có thì nhờ giáo vụ xếp tay |
| Đổi hình thức (lớp kết hợp) | Trước giờ học **2 giờ**, khi hình thức kia còn chỗ |
| Hàng chờ | Khi có người huỷ, chỗ được giữ **2 giờ** cho người đầu hàng; không nhận thì chuyển người kế |
| Lớp linh hoạt | Không có lịch cố định; học viên đặt từng buổi trong 4 tuần tới, không vượt số buổi còn trong gói |
| Buổi bị huỷ hoặc lớp kết thúc | Chỗ của mọi người bị huỷ; quyền học bù đã dùng ở buổi đó được trả lại |

Hai học viên cùng đặt chỗ cuối cùng một lúc: chỉ một người được, người kia nhận "Buổi này đã hết chỗ". Quản trị đổi các mốc trên ở [Quy tắc đặt buổi](quan-tri-trung-tam.md#quy-tắc-đặt-buổi).

## Ghi danh và sĩ số

- Học viên vào lớp **chỉ khi đơn hàng gắn lớp được trả đủ**. Không thêm tay vào lớp được.
- Chỗ trong lớp được giữ theo thứ tự thanh toán đủ. Lớp đã đủ **Sĩ số tối đa** thì đơn trả đủ sau đó chuyển sang **Cần hoàn tiền**.
- Sĩ số tối đa không giảm xuống dưới số học viên đang giữ chỗ.

## Hoàn tiền

| Trường hợp | Đơn ở trạng thái | Số tiền đề xuất |
| --- | --- | --- |
| Lớp không nhận được học viên (đầy) | Cần hoàn tiền | Toàn bộ số đã thu |
| Nghỉ khi chưa học buổi nào | Đã thanh toán | Toàn bộ số đã thu |
| Nghỉ giữa khoá | Đã thanh toán | Buổi **trả phí** chưa dùng × đơn giá một buổi (giá đơn ÷ số buổi trả phí) |
| Nghỉ khi chưa trả đủ | Chờ thanh toán (đã thu một phần) | Số đã thu |

- **Buổi tặng không hoàn.** Buổi cộng thêm bằng điều chỉnh không hoàn.
- Kế toán sửa được số tiền (ví dụ trừ phí theo chính sách trung tâm) nhưng không quá số đã thu; **lý do luôn bắt buộc** và được in trên phiếu chi.
- Mỗi đơn hoàn **một lần**, **không hoàn tác**. Sau khi hoàn: số buổi còn lại về 0 (dòng **Hoàn**), học viên được cho nghỉ lớp, chỗ được nhả.
- Phần chênh giữa giá trị buổi chưa dùng và số hoàn (phí giữ lại) được tính vào doanh thu ngày hoàn của cơ sở thu tiền.
- Ví dụ: gói 24 buổi trả phí + 2 tặng, giá 4.800.000 ₫ → đơn giá một buổi 200.000 ₫. Đã học 9 buổi, nghỉ: còn 15 buổi trả phí → đề xuất 3.000.000 ₫; 2 buổi tặng không hoàn.

## Phiếu thu, phiếu chi

- Số phiếu thu `PT-000001`, phiếu chi `PC-000001`, tăng dần trong trung tâm, không trùng, không nhảy số.
- Thanh toán nhập nhầm được **huỷ** (không xoá): phiếu vẫn còn, in ra có dấu **ĐÃ HỦY**.
- Mỗi lần in / tải được ghi nhật ký; một người in một phiếu tối đa 20 lần trong 24 giờ.

## Lịch học

- Thời gian lưu theo giờ quốc tế nhưng hiển thị và xếp theo **múi giờ của cơ sở** của lớp (ví dụ `Asia/Ho_Chi_Minh`).
- Xếp lịch sinh đủ số buổi của cấp độ, **bỏ ngày nghỉ** của cơ sở và toàn trung tâm. Ngày nghỉ thêm sau khi đã xếp **không** tự dời buổi.
- **Không trùng lịch**: một giáo viên hoặc một phòng không thể có hai buổi chồng giờ, kể cả ở hai lớp khác nhau, kể cả khi hai người xếp cùng lúc. Trùng thì không buổi nào được tạo / đổi.
- Hình thức lớp đổi được bất cứ lúc nào; áp cho các buổi chưa diễn ra.

## Phòng học trực tuyến có sẵn

- Tối đa **6 người mỗi phòng, gồm giáo viên** (mặc định; đội kỹ thuật đổi được), luôn giữ một chỗ cho giáo viên.
- Ổn định nhất khoảng **4 người bật camera**; từ 4 người trở lên hình tự giảm chất lượng.
- Một người chỉ ở một nơi; mở phòng ở tab / máy khác thì nơi cũ bị ngắt.
- Phòng tự đóng khi không còn ai sau giờ kết thúc, hoặc 30 phút sau giờ kết thúc.
- Chưa ghi hình. Tin nhắn chat không được lưu.

## Công và lương giáo viên

- Lớp online: công = số phút thực dạy theo dữ liệu phòng học, **tối đa bằng thời lượng kế hoạch**; số phút vào muộn được ghi lại.
- Lớp tại cơ sở: công = thời lượng kế hoạch, ghi khi giáo viên xác nhận điểm danh.
- Tiền = phút × đơn giá mỗi giờ ÷ 60, theo đơn giá có hiệu lực vào ngày dạy.
- Kỳ lương gom mọi công **nháp** đến ngày kết thúc kỳ (kể cả công đến muộn của kỳ trước). Hai kỳ không chồng ngày. **Chốt kỳ** đóng băng số tiền; đơn giá mới không làm thay đổi công đã duyệt / đã chốt.

## Phân quyền theo vai trò và cơ sở

Một người có thể giữ nhiều vai trò; quyền là hợp của các vai trò. Bạn chỉ thấy nút cho việc mình được làm; hệ thống cũng kiểm tra lại mỗi lần lưu (thiếu quyền báo "Bạn không có quyền thực hiện thao tác này" hoặc "Bạn không phụ trách cơ sở này").

| Việc | Quản trị | Quản lý cơ sở | Điều phối | Tư vấn | Kế toán | Giáo viên |
| --- | :-: | :-: | :-: | :-: | :-: | :-: |
| Lead: xem, gọi lại, sửa | ✓ | ✓ (cơ sở mình) | | ✓ | | |
| Học viên: thêm, sửa, ngừng dùng, hiện liên hệ | ✓ | ✓ | ✓ | ✓ | | |
| Học viên: xem danh sách (liên hệ che) | ✓ | ✓ | ✓ | ✓ | ✓ | |
| Xuất CSV, in danh sách học viên / lead | ✓ | ✓ | | | | |
| Lớp, lịch: mở, xếp, sửa, kết thúc | ✓ | ✓ (cơ sở mình) | ✓ (cơ sở mình) | | | |
| Học bù: xếp tay, từ chối; xem học viên xin nghỉ nhiều | ✓ | ✓ (cơ sở mình) | ✓ (cơ sở mình) | | | |
| Sửa quy tắc đặt buổi | ✓ | | | | | |
| Lớp: xem mọi lớp | ✓ | ✓ | ✓ | ✓ | ✓ | |
| Phòng học, ngày nghỉ cơ sở | ✓ | ✓ (cơ sở mình) | ✓ (cơ sở mình) | | | |
| Cơ sở, ngày nghỉ toàn trung tâm | ✓ | | | | | |
| Chương trình, cấp độ | ✓ | | | | | |
| Tạo đơn hàng | ✓ | | | ✓ | ✓ | |
| Gói học | ✓ | | | | ✓ (toàn trung tâm) | |
| Thanh toán, hoàn tiền, điều chỉnh buổi, in phiếu | ✓ | | | | ✓ (cơ sở mình) | |
| Đơn giá giáo viên, kỳ lương | ✓ | | | | ✓ | |
| Nhân sự, vai trò | ✓ | | | | | |
| Bảo mật tài khoản, nhật ký bảo mật | ✓ | | | | | |
| Quyền riêng tư, nhật ký truy cập | ✓ | | | | | |
| Nhật ký thông báo | ✓ | | | | | |
| Điểm danh, vào lớp dạy (trong `/teach`) | | | | | | ✓ (buổi mình dạy) |

Giáo viên chỉ có vai trò Giáo viên không vào được khu vực Vận hành (được chuyển về `/teach`).

"(cơ sở mình)": khi người đó được giới hạn theo cơ sở trong **Nhân sự**; không giới hạn thì là toàn trung tâm. Chỉ **Quản lý cơ sở**, **Điều phối**, **Kế toán** giới hạn được theo cơ sở.

> Thanh bên trái của khu vực Vận hành hiện giống nhau cho mọi vai trò (trừ 3 mục chỉ quản trị thấy). Mở mục ngoài quyền của mình có thể thấy trang trống (ví dụ **Kỳ lương** báo "Chưa có kỳ lương nào", **Nhân sự** trống) dù trung tâm có dữ liệu: đó là do bạn không có quyền xem, không phải mất dữ liệu.

## Dữ liệu cá nhân

- Danh sách luôn che liên hệ; người giữ hồ sơ bấm **Hiện** từng người; mọi lần hiện, xuất, in đều vào nhật ký truy cập.
- Xuất / in: tối đa 2.000 dòng mỗi lần, 20 lần mỗi người trong 24 giờ.
- Yêu cầu của chủ thể dữ liệu: hạn xử lý 72 giờ.
- Lead quá 24 tháng được hệ thống tự ẩn danh hoá.
- Chi tiết: [Xử lý dữ liệu cá nhân](../xu-ly-du-lieu-ca-nhan.md).

## Email tự động

Hệ thống tự gửi email, mỗi người theo ngôn ngữ của mình (học viên: **Ngôn ngữ liên lạc** trong hồ sơ; nhân viên: ngôn ngữ của tài khoản).

| Email | Khi nào | Ai nhận |
| --- | --- | --- |
| Lead mới | Có khách đăng ký tư vấn | Tư vấn viên được giao; trung tâm chưa có tư vấn viên thì các quản trị |
| Chào mừng học viên | Cấp tài khoản học viên | Học viên (email trong hồ sơ) |
| Xác nhận vào lớp | Học viên vào lớp sau khi trả đủ | Học viên |
| Sắp hết buổi | Còn ≤ 3 buổi | Học viên |
| Đổi giờ buổi học / Hủy buổi học | Một buổi bị đổi giờ hoặc huỷ (kể cả do sửa lịch tuần, kết thúc / huỷ lớp) | Học viên đang học của lớp và giáo viên của buổi |
| Nhắc lịch học | 24 giờ trước mỗi buổi | Học viên đang học của lớp và giáo viên của buổi |
| Link đặt lại mật khẩu | Nhân viên bấm Quên mật khẩu | Người yêu cầu |
| Chốt kỳ lương | Kỳ lương được chốt | Giáo viên có công trong kỳ |

Học viên không có email trong hồ sơ thì không nhận email. Chưa có: SMS, Zalo, thông báo trong ứng dụng, email mời nhân viên mới.
