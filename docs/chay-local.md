# Chạy và tắt Konnyaku trên máy dev

Hệ thống local gồm 3 phần: hạ tầng trong Docker, backend .NET (API + Worker), frontend Next.js (một app). Bật theo thứ tự dưới đây, tắt theo thứ tự ngược lại.

## Cổng sử dụng

| Thành phần | Cổng | Ghi chú |
| --- | --- | --- |
| Web (`frontend/apps/site`: trang công khai, đăng nhập, `/learn`, `/teach`, `/ops`) | **4000** | Mở trình duyệt ở đây. Bảng điều khiển nền tảng: `http://admin.localhost:4000/platform` |
| API | 5080 | Tài liệu API: `http://localhost:5080/scalar` |
| Worker | 5090 | Xử lý outbox (ghi danh sau thanh toán, tạo phòng học, trừ buổi, công) |
| PostgreSQL | 5433 | User `konnyaku` / `konnyaku_dev`, DB `konnyaku` |
| Signaling phòng học (`services/signaling`) | 5100 | WebSocket `ws://localhost:5100/rtc/ws`, sức khoẻ `/rtc/health`; container `signaling` trong docker compose |
| Redis | 6380 | Không bắt buộc: dev đếm giới hạn tần suất trong RAM (xem [Giới hạn tần suất, giám sát, bắt lỗi](#giới-hạn-tần-suất-giám-sát-bắt-lỗi)) |
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
| Kế toán (Accountant) | `accountant@review.example.com` | `/ops` | Đơn đã trả, trả một nửa, chưa trả, một đơn chờ hoàn tiền vì lớp đầy và một đơn đã hoàn (phiếu chi `PC-000001`). **Đã bật xác thực 2 bước**: seed in ra khoá bí mật (mỗi lần seed một khoá mới); lấy mã 6 số hiện tại bằng `python infra/scripts/seed_review.py --totp <khoá>` hoặc thêm khoá vào ứng dụng xác thực |
| Kế toán Cầu Giấy | `accountant.hn@review.example.com` | `/ops` | Kế toán giao theo cơ sở: chỉ thu tiền, hoàn tiền, in phiếu cho đơn của cơ sở Cầu Giấy |
| Giáo viên tiếng Nhật | `teacher.ja@review.example.com` | `/teach` | Lớp online `JP-N5-ON1` đã học khoảng 4 tuần, điểm danh đã xác nhận, công tháng này |
| Giáo viên tiếng Anh | `teacher.en@review.example.com` | `/teach` | Lớp offline `EN-A2-Q1` (phòng P101), điểm danh tay |
| Giáo viên tiếng Trung | `teacher.zh@review.example.com` | `/teach` | Lớp `ZH-HSK1-HN` khai giảng tuần sau |
| Học viên | `hv-review01` … `hv-review12` | `/learn` | `hv-review01`–`05`: lớp tiếng Nhật, số buổi đã trừ, video xem lại; `06`–`09`: lớp tiếng Anh offline; `10`: lớp tiếng Trung; `11`: đơn mới trả một nửa; `12`: đơn chưa trả |
| Phụ huynh | `ph-review01` | `/learn` | Chưa gắn với học viên nào (tính năng liên kết phụ huynh chưa có) |

Thử danh sách lớn (phân trang, tìm kiếm không dấu, giới hạn xuất CSV): thêm học viên giả vào trung tâm `review` (tên tiếng Việt ngẫu nhiên, SĐT ngẫu nhiên, email `hv.<n>@example.com`, khoảng 1 phút cho 1.000 người):

```bash
python infra/scripts/seed_students.py --count 1000
```

Bảo mật tài khoản khi chạy local (`appsettings.Development.json` của Api):

- `Auth:TwoFactor:Enforce` là `false`: Quản trị và Kế toán đăng nhập được bằng mật khẩu (để seed chạy). Muốn thử bắt buộc 2FA như production: chạy API với `Auth__TwoFactor__Enforce=true`.
- Quên mật khẩu: email "Đặt lại mật khẩu" đi qua module Notification như mọi email khác (đọc trong Mailpit, Worker phải chạy); địa chỉ link theo `Notification:SiteUrlTemplate`. Nhật ký `/ops/notifications` không hiện link.
- API cache kết quả kiểm tra phiên 30 giây (`Auth:SessionCacheSeconds`): khoá tài khoản hay đăng xuất ở nơi khác có hiệu lực trên process API khác trong tối đa chừng ấy thời gian; trang đang mở tự về trang đăng nhập trong khoảng một phút.
- Endpoint mô phỏng buổi học (`/api/workspace/dev/...`) chỉ bật ở Development; nơi khác cần `Workspace__DevTools=true`.
### Người vận hành nền tảng (super-admin)

`seed_review.py` cũng tạo người vận hành nền tảng đầu tiên (nếu database chưa có ai). Bảng điều khiển chỉ mở trên host quản trị `http://admin.localhost:4000/platform` (trên host trung tâm `/platform` trả 404):

| Mục | Giá trị (chỉ dùng cho dev) |
| --- | --- |
| Email | `superadmin@platform.example.com` |
| Mật khẩu | `Platform-Pass-123` |
| Secret TOTP (base32) | `KONNYAKUDEVPLATFORMTOTP234567ABC` — thêm vào ứng dụng xác thực (nhập khoá thủ công), hoặc in mã hiện tại: `python infra/scripts/seed_review.py --totp` |

Mỗi mã TOTP chỉ dùng được một lần (chờ 30 giây để có mã mới khi đăng nhập lại). Sai 5 lần thì khóa 15 phút; reset nhanh trên database dev: `UPDATE platform.operator SET failed_attempts = 0, locked_until = NULL;`. Host quản trị đổi được bằng `Platform:AdminHost` (API) và `KONNYAKU_ADMIN_HOST` (web), mặc định `admin.<BaseDomain>` và `admin.localhost`.

Biến môi trường `KONNYAKU_API` và `KONNYAKU_PLATFORM_KEY` cho phép chạy script vào API khác (ví dụ stack staging); tham số `--base-domain` khớp với `Platform:BaseDomain` của API đó.

### Xem email gửi đi (Mailpit)

Worker gửi email qua SMTP của Mailpit (`localhost:1025`, có sẵn trong `docker-compose.yml`, cấu hình ở `Notification:Smtp` trong `backend/src/Hosts/Konnyaku.Worker/appsettings.Development.json`). Mở `http://localhost:8025` để đọc. Ví dụ: trong `/ops/classes` đổi giờ một buổi → vài giây sau học viên (có email liên lạc) và giáo viên của buổi nhận email "Đổi giờ buổi …", mỗi người theo ngôn ngữ của mình. Nhật ký và nút gửi lại: `/ops/notifications` (chỉ TenantAdmin).

Link trong email dùng `Notification:SiteUrlTemplate` (dev: `http://{host}:4000`). Đổi Worker sang cổng/SMTP khác bằng biến môi trường, ví dụ `Notification__Smtp__Port=1026`, `Notification__SiteUrlTemplate=http://{host}:4300`. Nhắc lịch học chạy 24 giờ trước mỗi buổi; muốn thử ngay thì đổi giờ một buổi sang khoảng 20 giờ tới.

Dùng Claude Code: các dịch vụ trên đã khai báo trong `.claude/launch.json` (`api`, `worker`, `site`); bảo Claude "chạy dev server" là đủ. Hạ tầng Docker và migration vẫn phải chạy trước.

## Test giao diện tự động (Playwright)

`frontend/e2e` chạy các luồng chính trên Chromium thật, với một hệ thống riêng dựng từ đầu (không đụng database dev). CI chạy cùng script này trong job `e2e` của mỗi PR.

| Luồng (`frontend/e2e/tests`) | Kiểm tra |
| --- | --- |
| `public-lead` | Trang công khai `vi`/`en`, gửi form đăng ký → tư vấn viên thấy lead ở `/ops/leads` (SĐT bị che) |
| `realms` | Học viên và giáo viên đăng nhập cùng lúc một trình duyệt; đăng xuất một bên không ảnh hưởng bên kia, về đúng host trung tâm |
| `enrollment` | Quản trị tạo học viên + tài khoản → đơn → thu đủ tiền → Worker xếp lớp → học viên đăng nhập lần đầu, đổi mật khẩu, thấy lịch và số buổi |
| `attendance` | Giáo viên xác nhận điểm danh buổi đã học (phòng Fake) → số buổi của học viên giảm; giáo viên không vào được lead (giao diện, relay, API) |
| `refund` | Kế toán đăng nhập 2 bước, hoàn tiền đơn chờ hoàn (lớp đầy), tải phiếu thu PDF |
| `password-reset` | Quên mật khẩu → email trong Mailpit → đặt lại → đăng nhập bằng mật khẩu mới; link chỉ dùng một lần |
| `student-search` | Tìm "ngoc an" ra "Lê Ngọc Ân"; quản trị xem được liên hệ (có ghi nhật ký), kế toán chỉ thấy bản che |
| `platform` | Super-admin đăng nhập bằng mã TOTP trên `admin.localhost`, tạo trung tâm; quản trị viên mới phải đổi mật khẩu tạm |

Lần đầu cài trình duyệt: `npx -y pnpm@10.34.9 --dir frontend/e2e exec playwright install chromium`. Sau đó một lệnh (cần Docker, .NET 10, Python 3; từ thư mục gốc repo):

```bash
npx -y pnpm@10.34.9 --dir frontend e2e
```

Script `frontend/e2e/run.mjs` dựng PostgreSQL + Mailpit trong Docker (project `konnyaku-e2e`, database trống), build backend (Release) và web (`next build`) song song, chạy Migrator, bật Api/Worker/web, chạy `seed_review.py`, rồi `playwright test`; xong thì tắt và xoá hết. Khoảng 6–8 phút, phần lớn là build và seed. Tuỳ chọn:

| Lệnh | Dùng khi |
| --- | --- |
| `node frontend/e2e/run.mjs --no-build` | Chạy lại, giữ bản build lần trước |
| `node frontend/e2e/run.mjs -- --grep refund` | Sau `--` là tham số của `playwright test` (chỉ chạy một luồng, `--headed`, `--repeat-each 3`…) |
| `node frontend/e2e/run.mjs --serve` | Dựng và seed rồi giữ nguyên để viết test: ở terminal khác `cd frontend/e2e && npx playwright test` (hoặc `--ui`); Ctrl+C để tắt |

Cổng riêng, không trùng hệ thống dev: PostgreSQL 5450, Mailpit 1030/8030, API 5190, web 4950 (`http://review.localhost:4950`); đổi bằng biến `E2E_PG_PORT`, `E2E_SMTP_PORT`, `E2E_MAILPIT_PORT`, `E2E_API_PORT`, `E2E_WEB_PORT`. Python khác `python`/`python3`: đặt `PYTHON`. Môi trường test tắt captcha (`Captcha__Enabled=false`) và nới giới hạn tần suất form công khai / quên mật khẩu.

Khi test lỗi: `frontend/e2e/playwright-report` (mở bằng `npx playwright show-report` trong `frontend/e2e`) có trace, video, ảnh chụp; log server ở `frontend/e2e/logs/{api,worker,web}.log`. Trên CI các thư mục này là artifact `e2e-results` của job.

Thêm hoặc sửa luồng (bắt buộc với PR đổi điều người dùng thấy): đọc [frontend/e2e/README.md](../frontend/e2e/README.md) — chỗ đặt spec và fixture, đăng nhập theo vai trò, tạo dữ liệu, chạy một spec.

## Phòng học có sẵn (WebRTC)

1. Bước 1 ở trên đã bật container `signaling` (lần đầu build image Rust mất vài phút). Chạy riêng: `docker compose -f infra/docker/docker-compose.yml up -d --build signaling`. Container gọi API dev ở `http://host.docker.internal:5080` để báo vào/ra phòng.
2. Đổi provider của tenant demo (seed dùng `Fake`) bằng tài khoản quản trị: `PUT /api/workspace/connection` với `{"provider":"Konnyaku"}`. Buổi được xếp hoặc đổi giờ **sau đó** có link `/room/{mã}`; buổi cũ giữ phòng mô phỏng.
3. Thử nhiều người cùng lúc: mỗi realm một cookie, nên một trình duyệt giữ được một giáo viên và một học viên; người thứ ba dùng cửa sổ ẩn danh hoặc trình duyệt khác. `*.localhost` là ngữ cảnh an toàn nên camera/micro dùng được không cần HTTPS.
4. Không cần TURN khi thử trên một máy. Secret dùng chung giữa API (`Rtc:SignalingSecret` trong `appsettings.Development.json`) và container (`SIGNALING_SECRET`) phải giống nhau.
5. Test của service: cần Rust (hoặc chạy trong Docker), `cd services/signaling && cargo test && cargo clippy --all-targets -- -D warnings`.

## Giới hạn tần suất, giám sát, bắt lỗi

Ở dev cả ba đều **tắt hoặc chạy chế độ đơn giản**, không cần cấu hình gì:

| Phần | Dev mặc định | Thử như production |
| --- | --- | --- |
| Giới hạn tần suất (form công khai 10/phút/IP, quên/đặt lại mật khẩu 5/phút/IP) | Đếm trong RAM của process API | Chạy API với `Redis__ConnectionString=localhost:6380` (Redis của `docker-compose.yml`): bộ đếm dùng chung, khoá `kk:rl:*`. Tắt Redis giữa chừng: API tự đếm trong RAM, không lỗi. Vượt hạn mức: API trả `429` kèm `Retry-After` |
| Trace và metric (OpenTelemetry) | Tắt (không có `OTEL_EXPORTER_OTLP_ENDPOINT`) | Dùng stack production local bên dưới |
| Bắt lỗi (Sentry / GlitchTip) | Tắt (không có DSN) | `Sentry__Dsn=<dsn>` cho API/Worker, `SENTRY_DSN=<dsn>` cho web |

**Stack production local có giám sát** (đúng như server: Caddy → web ×2 → api ×2, worker, Redis, profile `monitoring`). Cách dựng stack production trên máy dev ở [toi-uu-tai-nguyen.md](toi-uu-tai-nguyen.md#cách-đo); thêm vào `.env` của stack thử:

```bash
COMPOSE_PROFILES=monitoring
OTEL_ENDPOINT=http://alloy:4318
GRAFANA_LOCAL_PORT=3301                      # Grafana: http://localhost:3301/grafana/ (admin / GRAFANA_ADMIN_PASSWORD)
GRAFANA_ROOT_URL=http://localhost:3301/grafana/
GRAFANA_COOKIE_SECURE=false                  # stack thử chạy HTTP
PROBE_PUBLIC_URL=http://caddy:80/api/health  # thay cho https://admin.BASE_DOMAIN khi Caddy chạy HTTP
SMTP_HOST=mailpit                            # cảnh báo và email ứng dụng vào một Mailpit trong cùng stack
SMTP_PORT=1025
SMTP_SECURITY=None
```

và một file override (`-f compose.yaml -f override.yaml`) đổi cổng Caddy (ví dụ `8091:80`), tắt `coturn` (`profiles: [disabled]`) và thêm service `mailpit` (`axllent/mailpit`, mạng `app` + `monitoring`, cổng web tuỳ chọn). Đặt tên project riêng (`docker compose -p konnyaku-obs …`) để không đụng stack khác. Thử nhanh:

- Trace: mở vài trang của trung tâm qua Caddy, rồi Grafana → Explore → Tempo → Search, service `konnyaku-web`: một trace gồm trang Next.js, `fetch` sang API, API và câu SQL.
- Cảnh báo: `docker stop <project>-api-2` → sau ~3 phút Mailpit nhận "FIRING ApiReplicaMissing"; `docker start` lại → "RESOLVED".
- Lần `up` đầu tiên trên volume mới, `migrator` và `postgres-monitor-role` có thể lỗi vì PostgreSQL còn đang khởi tạo: chạy `up -d` lần nữa.

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
| `429 Too Many Requests` khi thử form công khai / quên mật khẩu nhiều lần | Đúng giới hạn tần suất; chờ hết phút (header `Retry-After`) hoặc nâng `RateLimits__PublicFormsPerMinute` / `RateLimits__PasswordResetPerMinute` cho API dev |
| Seed báo tenant đã tồn tại | Database đã có dữ liệu demo; dùng tài khoản cũ hoặc `down -v` để làm lại |
| Bảng điều khiển nền tảng báo mã không đúng | Mã vừa dùng rồi (chờ mã mới sau 30 giây) hoặc đồng hồ máy lệch quá 30 giây |
| Trang trung tâm báo "tạm ngừng hoạt động" | Trung tâm bị tạm khóa trong bảng điều khiển nền tảng; mở lại ở `/platform` |
