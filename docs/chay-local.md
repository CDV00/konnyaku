# Chạy và tắt Konnyaku trên máy dev

Hệ thống local gồm 3 phần: hạ tầng trong Docker, backend .NET (API + Worker), frontend Next.js (một app). Bật theo thứ tự dưới đây, tắt theo thứ tự ngược lại.

## Cổng sử dụng

| Thành phần | Cổng | Ghi chú |
| --- | --- | --- |
| Web (`frontend/apps/site`: trang công khai, đăng nhập, `/learn`, `/teach`, `/ops`) | **4000** | Mở trình duyệt ở đây |
| API | 5080 | Tài liệu API: `http://localhost:5080/scalar` |
| Worker | 5090 | Xử lý outbox (ghi danh sau thanh toán, tạo phòng học, trừ buổi, công) |
| PostgreSQL | 5433 | User `konnyaku` / `konnyaku_dev`, DB `konnyaku` |
| Signaling phòng học (`services/signaling`) | 5100 | WebSocket `ws://localhost:5100/rtc/ws`, sức khoẻ `/rtc/health`; container `signaling` trong docker compose |
| Redis | 6380 | |
| Mailpit (xem email gửi đi) | 8025 (web), 1025 (SMTP) | `http://localhost:8025` |

Frontend dùng 4000 để tránh trùng các dự án khác hay chạy ở 3000/3001. Cần đổi thì sửa đồng thời: `frontend/apps/site/package.json` (script `dev`, `start`), `.claude/launch.json` và tài liệu này.

## Cần cài sẵn

.NET SDK 10, Node 20+, Docker Desktop, Python 3. Nếu lệnh `pnpm` báo lỗi corepack, dùng `npx -y pnpm@10.34.9` thay cho `pnpm` ở mọi lệnh bên dưới.

## Bật hệ thống

Mỗi lệnh chạy lâu (API, Worker, frontend) cần một cửa sổ terminal riêng. Mọi lệnh chạy từ thư mục gốc repo.

1. Hạ tầng:

   ```bash
   docker compose -f infra/docker/docker-compose.yml up -d
   ```

2. Tạo/cập nhật bảng (chạy lại mỗi khi có migration mới):

   ```bash
   dotnet run --project backend/src/Hosts/Konnyaku.Migrator
   ```

3. API (terminal 1):

   ```bash
   dotnet run --project backend/src/Hosts/Konnyaku.Api --launch-profile http
   ```

4. Worker (terminal 2):

   ```bash
   dotnet run --project backend/src/Hosts/Konnyaku.Worker
   ```

5. Frontend (terminal 3):

   ```bash
   cd frontend && npx -y pnpm@10.34.9 install && npx -y pnpm@10.34.9 dev
   ```

6. Dữ liệu demo, **một lần** trên database mới (cần API và Worker đang chạy):

   ```bash
   python infra/scripts/seed_demo.py
   ```

   Script in ra mật khẩu tạm của học viên `hv000001`; lưu lại vì chỉ hiện một lần.

7. Mở `http://demo.localhost:4000`. Trình duyệt tự trỏ `*.localhost` về máy mình; trung tâm được xác định theo tên host (`demo`).

| Vai trò | Đăng nhập | Vào |
| --- | --- | --- |
| Quản trị | tab Giáo viên · Nhân viên: `admin@demo.example.com` / `Admin-Pass-123` | `/ops` |
| Giáo viên | tab Giáo viên · Nhân viên: `teacher@demo.example.com` / `Teacher-Pass-123` | `/teach` |
| Học viên | tab Học viên · Phụ huynh: `hv000001` / mật khẩu tạm từ bước 6 | `/learn` (đổi mật khẩu lần đầu) |

### Dữ liệu đầy đủ để review (`seed_review.py`)

`seed_demo.py` chỉ tạo vừa đủ để chạy thử một luồng. Để review mọi màn hình và mọi vai trò, chạy thêm (cùng database, tạo trung tâm thứ hai `review`; mất khoảng 1–2 phút vì chờ Worker xử lý event):

```bash
python infra/scripts/seed_review.py
```

Mở `http://review.localhost:4000/login`. Mật khẩu chung: nhân viên `Review-Pass-123`, học viên và phụ huynh `Learner-Pass-123` (không bị bắt đổi mật khẩu).

| Vai trò | Tài khoản | Vào | Xem được gì |
| --- | --- | --- | --- |
| Quản trị trung tâm (TenantAdmin) | `admin@review.example.com` | `/ops` | Mọi màn vận hành, kỳ lương tháng trước đã chốt, tháng này đang mở |
| Quản lý cơ sở (BranchManager) | `manager@review.example.com` | `/ops` | 3 cơ sở (Online, Quận 1, Cầu Giấy), phòng học, ngày nghỉ |
| Điều phối (Coordinator) | `coordinator@review.example.com` | `/ops` | 5 lớp; lớp `KO-T1-ON3` chưa xếp lịch để thử xếp |
| Tư vấn (Consultant) | `consultant@review.example.com` | `/ops` | Lead ở đủ trạng thái (mới, đã liên hệ, đã chuyển đổi, mất) |
| Kế toán (Accountant) | `accountant@review.example.com` | `/ops` | Đơn đã trả, trả một nửa, chưa trả, và một đơn chờ hoàn tiền vì lớp đầy |
| Giáo viên tiếng Nhật | `teacher.ja@review.example.com` | `/teach` | Lớp online `JP-N5-ON1` đã học khoảng 4 tuần, điểm danh đã xác nhận, công tháng này |
| Giáo viên tiếng Anh | `teacher.en@review.example.com` | `/teach` | Lớp offline `EN-A2-Q1` (phòng P101), điểm danh tay |
| Giáo viên tiếng Trung | `teacher.zh@review.example.com` | `/teach` | Lớp `ZH-HSK1-HN` khai giảng tuần sau |
| Học viên | `hv-review01` … `hv-review12` | `/learn` | `hv-review01`–`05`: lớp tiếng Nhật, số buổi đã trừ, video xem lại; `06`–`09`: lớp tiếng Anh offline; `10`: lớp tiếng Trung; `11`: đơn mới trả một nửa; `12`: đơn chưa trả |
| Phụ huynh | `ph-review01` | `/learn` | Chưa gắn với học viên nào (tính năng liên kết phụ huynh chưa có) |

Thử danh sách lớn (phân trang, tìm kiếm không dấu, giới hạn xuất CSV): thêm học viên giả vào trung tâm `review` (tên tiếng Việt ngẫu nhiên, SĐT ngẫu nhiên, email `hv.<n>@example.com`, khoảng 1 phút cho 1.000 người):

```bash
python infra/scripts/seed_students.py --count 1000
```

Biến môi trường `KONNYAKU_API` và `KONNYAKU_PLATFORM_KEY` cho phép chạy script vào API khác (ví dụ stack staging); tham số `--base-domain` khớp với `Platform:BaseDomain` của API đó.

Dùng Claude Code: các dịch vụ trên đã khai báo trong `.claude/launch.json` (`api`, `worker`, `site`); bảo Claude "chạy dev server" là đủ. Hạ tầng Docker và migration vẫn phải chạy trước.

## Phòng học có sẵn (WebRTC)

1. Bước 1 ở trên đã bật container `signaling` (lần đầu build image Rust mất vài phút). Chạy riêng: `docker compose -f infra/docker/docker-compose.yml up -d --build signaling`. Container gọi API dev ở `http://host.docker.internal:5080` để báo vào/ra phòng.
2. Đổi provider của tenant demo (seed dùng `Fake`) bằng tài khoản quản trị: `PUT /api/workspace/connection` với `{"provider":"Konnyaku"}`. Buổi được xếp hoặc đổi giờ **sau đó** có link `/room/{mã}`; buổi cũ giữ phòng mô phỏng.
3. Thử nhiều người cùng lúc: mỗi realm một cookie, nên một trình duyệt giữ được một giáo viên và một học viên; người thứ ba dùng cửa sổ ẩn danh hoặc trình duyệt khác. `*.localhost` là ngữ cảnh an toàn nên camera/micro dùng được không cần HTTPS.
4. Không cần TURN khi thử trên một máy. Secret dùng chung giữa API (`Rtc:SignalingSecret` trong `appsettings.Development.json`) và container (`SIGNALING_SECRET`) phải giống nhau.
5. Test của service: cần Rust (hoặc chạy trong Docker), `cd services/signaling && cargo test && cargo clippy --all-targets -- -D warnings`.

## Tắt hệ thống

1. Frontend, Worker, API: bấm **Ctrl+C** trong từng terminal.
2. Hạ tầng Docker:

   | Muốn | Lệnh | Dữ liệu |
   | --- | --- | --- |
   | Tạm dừng, lần sau bật lại nhanh | `docker compose -f infra/docker/docker-compose.yml stop` | Giữ nguyên |
   | Tắt và gỡ container | `docker compose -f infra/docker/docker-compose.yml down` | Giữ nguyên (nằm trong volume) |
   | Xoá sạch để làm lại từ đầu | `docker compose -f infra/docker/docker-compose.yml down -v` | **Mất hết** database và file MinIO |

   Sau `down -v` phải chạy lại từ bước 1, gồm cả migration và seed. Cần `down -v` khi thêm module mới (script tạo schema/role chỉ chạy lúc tạo volume).

### Cổng vẫn bị chiếm sau khi tắt

Terminal đóng ngang có thể để lại tiến trình chạy ngầm. Tìm và tắt theo cổng:

Windows (PowerShell):

```powershell
Get-NetTCPConnection -LocalPort 4000,5080,5090 -State Listen -ErrorAction SilentlyContinue | ForEach-Object { Stop-Process -Id $_.OwningProcess -Force }
```

macOS / Linux:

```bash
for p in 4000 5080 5090; do lsof -ti tcp:$p | xargs -r kill; done
```

## Lỗi thường gặp khi chạy

| Hiện tượng | Cách xử lý |
| --- | --- |
| API báo không kết nối được PostgreSQL | Docker chưa bật hoặc container `postgres` chưa healthy: `docker compose -f infra/docker/docker-compose.yml ps` |
| `relation ... does not exist` | Chưa chạy Migrator (bước 2) |
| Đã thanh toán nhưng học viên chưa vào lớp, không có phòng học | Worker chưa chạy (bước 4) |
| Trang báo không xác định được trung tâm | Đang mở `localhost:4000` thay vì `demo.localhost:4000`, hoặc chưa chạy seed |
| Build frontend lỗi `require() of ES Module` | Tailwind bị nâng khỏi 4.1.18; trả về đúng bản đó |
| Seed báo tenant đã tồn tại | Database đã có dữ liệu demo; dùng tài khoản cũ hoặc `down -v` để làm lại |
