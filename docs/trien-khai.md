# Triển khai Konnyaku lên VPS

Pilot chạy trên **một VPS** bằng Docker Compose. Toàn bộ định nghĩa nằm trong `infra/deploy/`. Image do GitHub Actions build và đẩy lên GHCR (`.github/workflows/deploy.yml`), cho cả **amd64** và **arm64**: cùng một tag, Docker trên server tự kéo bản đúng với CPU.

## Kiến trúc

```
Internet ─80/443─► Caddy ──► web ×2 (Next.js: trang công khai, /learn, /teach, /ops, /room; /platform trên admin.BASE_DOMAIN)
                    │   │       │  (mạng app)
                    │   │       ▼
                    │   │    api ×2 (ASP.NET Core) ──┐
                    │   │       ▲          │ PUBLISH  ├─► PostgreSQL 17 (mạng data, internal)
                    │   │       │ vào/ra   ▼ user:{id}│
                    │   │       │        redis ◄──────┤ (worker cũng PUBLISH sau khi commit)
                    │   │       │          │ SUBSCRIBE user:*
                    │   └─/rtc/*, /rt─► signaling ×1 (Rust, WebSocket: phòng học + realtime)
                    │        worker ×1 ──────────────┘
                    │        migrator (chạy mỗi lần up, xong thì thoát)
                    │        backup (pgBackRest) ── WAL + backup, mã hóa ──► object storage S3 ở nhà cung cấp khác
                    │        files-backup (rclone) ── bản sao bucket, mã hóa ──► cùng object storage đó
                    ├─files.BASE_DOMAIN─► garage (S3, kho file) ◄── api / worker (http://garage:3900)
                    └─ chứng chỉ HTTPS cấp tự động cho từng host tenant
Internet ─3478 + dải UDP─► coturn (TURN, mạng host)
Trình duyệt ◄────── hình/tiếng trực tiếp giữa các máy (WebRTC), qua coturn khi mạng chặn ──────► Trình duyệt
```

| Thành phần | Bản sao | Ghi chú |
| --- | --- | --- |
| `caddy` | 1 | Cổng duy nhất mở ra Internet. HTTPS tự động (on-demand) cho `{slug}.BASE_DOMAIN`, tên miền riêng và `admin.BASE_DOMAIN`; chia tải round-robin giữa các bản sao `web`, bỏ qua bản sao đang khởi động lại |
| `web` | `WEB_REPLICAS` (2) | Không lưu trạng thái. Gọi API qua mạng nội bộ, chuyển IP khách trong `X-Forwarded-For` |
| `api` | `API_REPLICAS` (2) | Không mở ra Internet. Chỉ tin `X-Forwarded-For` từ mạng `app` (`APP_SUBNET`). Cache tenant trong bộ nhớ 1 phút |
| `worker` | `WORKER_REPLICAS` (1) | Chạy nhiều bản an toàn: outbox lấy việc bằng `FOR UPDATE SKIP LOCKED` |
| `migrator` | — | Áp migration bằng role chủ schema `konnyaku`; `api`/`worker` chỉ khởi động khi migrator thành công |
| `postgres` | 1 | Image `konnyaku-postgres:17` (postgres:17 + pgBackRest, build trên server từ `postgres/Dockerfile`, tag riêng nên deploy không khởi động lại DB). Mạng `data` không có đường ra Internet; mạng `backup` chỉ để đẩy WAL ra object storage (không mở cổng nào). Mỗi module một role `kk_<module>` với mật khẩu riêng |
| `backup` | 1 | pgBackRest: full hằng tuần, diff hằng ngày, WAL liên tục, diễn tập khôi phục hằng tháng, ghi file trạng thái cho giám sát ([Backup](#backup)) |
| `signaling` | **1** | Cổng thời gian thực (`services/signaling`, Rust, image `konnyaku-signaling`), qua Caddy, chỉ nhận vé ngắn hạn do API ký (`SIGNALING_SECRET`). **Phòng học** `/rtc/ws`: giữ ai đang ở phòng nào **trong bộ nhớ** nên chỉ chạy một bản, báo vào/ra/kết thúc về API qua mạng nội bộ. **Realtime** `/rt`: một WebSocket mỗi tab đã đăng nhập, nhận tín hiệu từ Redis (`user:{id}`) và đẩy xuống tab của người đó ([Thông báo trong app](#thông-báo-trong-app-và-thời-gian-thực)) |
| `redis` | 1 | Bộ đếm giới hạn tần suất dùng chung cho mọi bản sao API, và pub/sub tín hiệu realtime (API, worker `PUBLISH`; signaling `PSUBSCRIBE user:*`). Mạng `data`, cổng 6379 chỉ trong mạng nội bộ, mật khẩu `REDIS_PASSWORD`, không lưu xuống đĩa. Redis dừng thì mỗi bản sao tạm đếm riêng trong RAM, chuông thông báo chuyển sang hỏi lại mỗi 30 giây; không lỗi request, không mất thông báo |
| `garage` | 1 (`GARAGE_REPLICAS`) | Kho file của module Content (Garage, S3, Rust). Trình duyệt tải lên/xuống thẳng qua Caddy ở `files.BASE_DOMAIN` bằng URL ký hạn ngắn; file không đi qua `web` hay `api` ([Kho file](#kho-file-garage)) |
| `files-backup` | 1 | Bản sao bucket hằng ngày lên object storage backup, mã hoá phía client (rclone) |
| `coturn` | 1 | TURN cho người dùng sau NAT/tường lửa chặt. Dùng mạng host (`network_mode: host`). Mật khẩu tạm theo TURN REST API (`TURN_SECRET`, hạn 6 giờ); không chuyển tiếp tới địa chỉ nội bộ/loopback |

Giới hạn tần suất (form công khai `RateLimits:PublicFormsPerMinute`, quên/đặt lại mật khẩu `RateLimits:PasswordResetPerMinute`, theo IP mỗi phút) đếm trong **Redis**, nên đúng với mọi số bản sao API; khoá trong Redis chỉ chứa mã băm của IP. Không cấu hình `Redis:ConnectionString` (dev, test) thì mỗi process đếm trong RAM như trước.

Cache "phiên đăng nhập còn hiệu lực" (`SessionValidationMiddleware`, `Auth:SessionCacheSeconds` = 30 s) **vẫn ở RAM từng bản sao**, có chủ ý: chuyển sang Redis chỉ rút thời gian một phiên bị thu hồi còn dùng được trên bản sao khác từ ≤ 30 s xuống 0, nhưng thêm một lượt gọi Redis vào **mọi** request đã đăng nhập và biến Redis thành phụ thuộc của việc đăng nhập (Redis dừng = phải chọn giữa từ chối hết hoặc bỏ qua kiểm tra). Số truy vấn DB không đổi (cả hai cách đều hỏi DB một lần mỗi 30 s mỗi phiên). Xem lại khi cần thu hồi tức thì (ví dụ khóa tài khoản bị chiếm) hoặc khi có ≥ 4 bản sao. Các thành phần giám sát (profile `monitoring`) ở mục [Giám sát và cảnh báo](#giám-sát-và-cảnh-báo).

### Phòng học có sẵn (WebRTC mesh)

Thay cho Google Meet khi trung tâm chưa có Google Workspace. Trung tâm chọn bằng `PUT /api/workspace/connection` với `{"provider":"Konnyaku"}` (không cần thêm host account; đổi từ `Fake` sang được, buổi đã tạo phòng giữ link cũ).

- **Kiến trúc**: P2P dạng mesh, không SFU — mỗi người gửi hình/tiếng tới từng người khác. Ổn khoảng **4 người bật camera**; mặc định `ROOM_CAPACITY=6` (gồm giáo viên, luôn giữ một chỗ cho giáo viên), trình duyệt tự giảm bitrate khi phòng đông. Cần lớp đông hơn thì phải thêm SFU (việc sau).
- **Vào phòng**: link **Vào lớp** → API kiểm quyền → `/room/{mã}`. Trang phòng xin **vé** (`POST /api/workspace/rooms/{mã}/ticket`, JWT HS256 hạn 2 phút: tenant, buổi, người, vai trò, giờ kết thúc buổi) mỗi lần kết nối và kết nối lại; signaling chỉ nhận vé này.
- **Điểm danh, bảng công**: signaling gửi vào/ra/kết thúc tới `POST /api/workspace/internal/rooms/events` (mạng nội bộ, header `X-Rtc-Token` ký bằng `SIGNALING_SECRET`, thử lại khi API lỗi). Khi phòng kết thúc, Workspace phát `ParticipantsCollectedV1` như mọi provider → Learning tạo điểm danh nháp, Hr tạo bảng công. Phòng tự đóng khi trống sau giờ kết thúc, hoặc sau `ROOM_GRACE_MINUTES` (30) phút.
- **Khởi động lại signaling** (deploy) ngắt mọi phòng vài giây; trình duyệt tự kết nối lại, thời gian có mặt không bị tính trùng.
- **Chưa có**: ghi hình (kho file đã có, phần ghi hình chưa làm), TURN qua TLS cổng 443 (mạng chỉ cho phép HTTPS sẽ không kết nối được), SFU cho lớp đông, nhiều bản signaling.

### Thông báo trong app và thời gian thực

Thiết kế ở [doc 10.9](10-hoc-tap-va-danh-gia.md#109-nhắn-tin-và-thông-báo-thời-gian-thực): không có SignalR trong API; thông báo **luôn ghi PostgreSQL trước**, socket chỉ báo "có mới".

- **Hộp thông báo**: mỗi sự kiện (đổi giờ / hủy / nhắc buổi, ghi danh, sắp hết buổi, chỗ trống từ danh sách chờ, học bù được duyệt, bài mới, sắp hết hạn nộp, có điểm, lead mới, chốt lương) thành một dòng `notification.inbox_item` của tài khoản người nhận (RLS theo tenant). Học viên chưa có tài khoản chỉ nhận email như trước.
- **Bộ định tuyến kênh**: trong app luôn bật; email và Web Push theo cài đặt từng người (`notification.notification_preference`, mặc định bật). Email cùng loại cho cùng người trong **10 phút** gộp thành một thư (`Notification__DigestMinutes`).
- **Tín hiệu**: sau khi commit, process đã ghi (worker khi xử lý outbox, API khi đánh dấu đã đọc) `PUBLISH user:{id} {"t":"inbox","id":"…","seq":812}` lên Redis. Mỗi bản `signaling` `PSUBSCRIBE user:*` và đẩy xuống socket `/rt` của người đó; tab gọi `GET /api/notification/inbox/sync?since=<seq>` qua web để lấy nội dung. Chạy nhiều bản api, worker và signaling không cần gì thêm (mỗi bản realtime nhận mọi tín hiệu, bỏ qua người không kết nối tới nó).
- **Vé**: tab xin `POST /api/notification/realtime/ticket` (JWT HS256 hạn 60 s, `aud=konnyaku-realtime`, chỉ id người dùng và tenant; cùng `SIGNALING_SECRET` với vé phòng học nhưng không dùng thay nhau được). Socket tự đóng sau `RT_CONNECTION_MINUTES` (60) để tab xin vé mới, nên phiên đã đăng xuất ngừng nhận tín hiệu trong tối đa 1 giờ (tín hiệu chỉ có id, nội dung vẫn phải qua API).
- **Mất kết nối**: tab tự kết nối lại (1 → 30 s) và đồng bộ theo con trỏ; trong lúc mất, chuông hỏi lại mỗi 30 giây. Redis kết nối lại thì signaling gửi `{"t":"resync"}` cho mọi tab. Thay image `signaling` khi deploy cũng chỉ làm các tab nối lại vài giây.
- **Web Push**: chuẩn VAPID, không cần dịch vụ ngoài. Cặp khoá `VAPID_PUBLIC_KEY` / `VAPID_PRIVATE_KEY` do `gen-secrets.sh` sinh (server cũ: chạy lại script rồi chép hai dòng này vào `.env`; để trống = tắt Web Push, trong app và email vẫn chạy). Đổi cặp khoá thì mọi trình duyệt phải bật lại. Tin đẩy **không mang nội dung**: service worker `/sw.js` hỏi web (đã đăng nhập) thông báo chưa đọc mới nhất để hiện. Worker gửi; trình duyệt trả 404/410 thì xoá đăng ký. Đăng ký đẩy là dữ liệu cá nhân: xoá khi đăng xuất trên trình duyệt đó và khi xoá dữ liệu học viên.
- **Kiểm tra**: `docker compose exec caddy wget -qO- http://signaling:5100/rt/health` (`realtime.connections`, `realtime.redis`); metric `konnyaku_realtime_connections`, `konnyaku_realtime_redis_up`.

## Chuẩn bị (một lần)

1. **VPS**: Ubuntu 24.04, 4 vCPU / 8 GB RAM / 100 GB SSD, x86 hoặc ARM (ví dụ Oracle Cloud Always Free, Ampere A1). Máy test 2 GB RAM (1–2 vCPU) chạy được với hồ sơ **máy nhỏ** trong `.env.example` (1 web, 1 api chạy luôn outbox, không có container worker); số đo và cách chọn tham số ở [toi-uu-tai-nguyen.md](toi-uu-tai-nguyen.md). Cài Docker Engine và plugin compose theo hướng dẫn chính thức của Docker. Mở firewall (cả firewall của nhà cung cấp cloud, ví dụ Security List của Oracle):

   | Cổng | Giao thức | Dùng cho |
   | --- | --- | --- |
   | 22 | TCP | SSH |
   | 80, 443 | TCP | Web, cấp chứng chỉ, signaling phòng học (`/rtc/*`), socket realtime (`/rt`) |
   | 443 | UDP | HTTP/3 |
   | 3478 | UDP + TCP | STUN/TURN của phòng học (coturn) |
   | 49160–49260 | UDP | Dải relay của TURN (`TURN_MIN_PORT`–`TURN_MAX_PORT`, khoảng 1 cổng cho mỗi kết nối được relay) |

   Máy cloud chỉ thấy IP nội bộ (AWS, GCP, Oracle): đặt `TURN_EXTERNAL_IP` trong `.env` là IP công khai (hoặc `công-khai/nội-bộ`).
2. **DNS**: bản ghi `A` cho `BASE_DOMAIN` và `*.BASE_DOMAIN` trỏ về IP VPS. Nếu dùng Cloudflare, để **DNS only** (đám mây xám): Caddy tự cấp chứng chỉ qua HTTP-01.
3. **Đăng nhập GHCR trên server** (image private): tạo GitHub token chỉ có quyền `read:packages`, rồi:

   ```bash
   echo "<token>" | docker login ghcr.io -u <github-user> --password-stdin
   ```

4. **Thư mục và bí mật**:

   ```bash
   sudo mkdir -p /opt/konnyaku && sudo chown "$USER" /opt/konnyaku && cd /opt/konnyaku
   ```

   Chép `infra/deploy/` lên (workflow deploy cũng tự chép mỗi lần chạy), rồi:

   ```bash
   cp .env.example .env && ./scripts/gen-secrets.sh >> .env && chmod 600 .env
   ```

   Server đã chạy từ trước bản có kho file: thêm `DB_PASSWORD_CONTENT`, `GARAGE_RPC_SECRET`, `GARAGE_ADMIN_TOKEN`, `S3_ACCESS_KEY`, `S3_SECRET_KEY` (chạy lại `gen-secrets.sh` rồi chỉ chép năm dòng đó) và tạo role `kk_content` bằng tay (mục [Kho file](#kho-file-garage)).

   Server đã chạy từ trước bản có Redis và giám sát: thêm `REDIS_PASSWORD`, `GRAFANA_ADMIN_PASSWORD`, `DB_PASSWORD_MONITOR` (chạy lại `gen-secrets.sh` rồi chỉ chép ba dòng đó), nếu không `docker compose up` báo thiếu biến. Server đã chạy từ trước bản có hộp thông báo: chép thêm `VAPID_PUBLIC_KEY` và `VAPID_PRIVATE_KEY` (không bắt buộc: thiếu thì Web Push tắt).

   Sửa trong `.env`: `BASE_DOMAIN`, `ACME_EMAIL`, `IMAGE_*` (thay `OWNER` bằng tên GitHub org/user viết **chữ thường**, ví dụ `ghcr.io/cdv00/konnyaku-web`). Server đã chạy từ trước bản có phòng học: thêm `SIGNALING_SECRET` và `TURN_SECRET` (chạy lại `gen-secrets.sh` rồi chỉ chép hai dòng đó) và `IMAGE_SIGNALING`. Lưu một bản `.env` vào trình quản lý mật khẩu của nhóm: mất file này là mất mật khẩu DB và khóa ký JWT. `gen-secrets.sh` cũng sinh `CAPTCHA_KEY` (xem mục [Captcha của form công khai](#captcha-của-form-công-khai)); `.env` có sẵn từ trước thì thêm tay một dòng `CAPTCHA_KEY=$(openssl rand -hex 32)`, nếu không `docker compose up` báo thiếu biến. Tương tự `BACKUP_CIPHER_PASS` (khoá mã hoá backup, xem [Backup](#backup)) — khoá này phải có một bản **ngoài server**.

5. **GitHub**: Settings → Environments, tạo `staging` và `production`. Mỗi environment có secrets `DEPLOY_HOST`, `DEPLOY_USER`, `DEPLOY_SSH_KEY` (khóa riêng của một user chỉ dùng để deploy, thuộc nhóm `docker`), `DEPLOY_KNOWN_HOSTS` (kết quả `ssh-keyscan -H <host>`), và biến `DEPLOY_PATH` nếu khác `/opt/konnyaku`. Nên bật *Required reviewers* cho `production`.

## Deploy

- Mỗi PR: CI build 5 image (api, worker, migrator, web, signaling) trên máy amd64 và máy arm64 thật của GitHub rồi chạy thử (`infra/deploy/scripts/smoke-test.sh`, kể cả web dừng êm khi nhận SIGTERM), build image PostgreSQL + pgBackRest và kiểm tra cú pháp các script, không đẩy lên registry.
- Merge vào `main` → CI xanh → workflow **Deploy** build lại trên từng kiến trúc, chạy thử, đẩy `<sha>-amd64` / `<sha>-arm64`, gộp thành tag đa kiến trúc `<sha>` và `latest`, rồi deploy lên **staging**.
- Lên **production**: Actions → Deploy → Run workflow → `production`.
- Workflow chép `compose.yaml`, `Caddyfile`, `caddy/`, `postgres/`, `scripts/`, `monitoring/` lên server rồi chạy **`./scripts/deploy.sh <sha>`** (deploy cuốn chiếu, bên dưới). Deploy tay trên server cũng chỉ là lệnh đó.

### Deploy cuốn chiếu (không gián đoạn)

`scripts/deploy.sh <tag>` ghi `IMAGE_TAG=<tag>` vào `.env` rồi:

1. Kéo image của tag; build `konnyaku-postgres:17` nếu server chưa có.
2. Hạ tầng (`postgres`, `backup`, `redis`, `caddy`, `coturn`, giám sát nếu bật) chỉ được tạo lại khi định nghĩa của nó trong `compose.yaml` đổi (với `postgres` là một lần khởi động lại DB, vài giây lỗi — báo trước khi deploy bản như vậy). `Caddyfile` được nạp lại êm (`caddy reload`, không cắt kết nối).
3. **Migrator chạy trước**. Lỗi thì dừng, chưa bản sao nào bị đổi.
4. `api`, rồi `web`, rồi `worker`: **từng bản sao một** — tạo một bản mới bên cạnh, chờ healthcheck báo `healthy` (tối đa `DEPLOY_HEALTH_TIMEOUT`, 180 s), rồi mới dừng một bản cũ. Bản mới không healthy thì bị xoá, script dừng và **bản cũ vẫn phục vụ**. Lúc cao nhất có thêm một bản sao (máy 2 GB: thêm ~150–400 MiB trong vài chục giây).
5. Bản cũ không bị cắt ngang request nào:
   - **Caddy → web**: Caddy tìm các bản sao web qua Docker DNS (`caddy/web-upstreams.caddy`), mà DNS liệt kê một container ngay khi nó *khởi động* (trước khi nghe cổng) và tới tận khi nó *biến mất*, Caddy còn giữ danh sách 5 s. Một POST gửi vào khe đó không thử lại được (Caddy gửi lại với thân rỗng). Nên trong lúc cuốn chiếu web, script ghi vào file đó **danh sách tĩnh** các bản đang phục vụ rồi `caddy reload` (êm, request đang chạy không bị cắt): bản mới chỉ vào danh sách khi đã healthy, bản cũ ra khỏi danh sách trước khi bị dừng; cuối cùng trả lại tra DNS (cả khi script lỗi giữa chừng).
   - **web → api**: Node tra DNS ở mỗi kết nối mới, không có khe như trên. Khi dừng (SIGTERM, `stop_grace_period: 30s`) API **phục vụ thêm 5 s** (`Shutdown__DrainSeconds`) với `Connection: close` để web bỏ các kết nối keep-alive đang giữ sau một response trọn vẹn, rồi Kestrel mới thôi nhận kết nối và trả nốt request đang chạy. `apiFetch` thử lại (tối đa 5 lần trong ~1,5 s) khi kết nối bị từ chối — request chưa tới API nên an toàn với mọi method; kết nối đứt giữa chừng chỉ thử lại với GET.
   - **web dừng**: `frontend/graceful-shutdown.cjs` thay cho handler của Next.js (vốn thoát ngay, cắt request đang chạy): cũng 5 s `Connection: close`, rồi đóng cổng, trả nốt request và thoát 0. Caddy tự đóng kết nối rảnh tới web sau 3 s (`keepalive 3s`), luôn trước web, nên không bao giờ gửi request vào kết nối web đang đóng.

| Thành phần | Cách đổi bản |
| --- | --- |
| `api`, `web`, `worker` | Cuốn chiếu như trên. Worker chạy song song hai bản an toàn (`FOR UPDATE SKIP LOCKED`, inbox chống trùng). Máy 2 GB (`WORKER_REPLICAS=0`) bỏ qua worker |
| `migrator` | Chạy một lần trước khi đổi bản; trong lúc cuốn chiếu, bản cũ và bản mới cùng chạy trên schema mới (xem quy tắc dưới) |
| `signaling` | Giữ phòng học trong bộ nhớ nên không chạy hai bản được. Chỉ thay khi image đổi **và không có phòng nào đang mở** (hỏi `/rtc/health`); đang có lớp thì giữ bản cũ và in lệnh chạy lại sau giờ học: `./scripts/deploy.sh --no-pull --signaling now`. Thay khi có lớp thì mọi phòng mất kết nối vài giây rồi trình duyệt tự nối lại (thời gian có mặt không bị tính trùng). Vì vậy thay đổi giao thức giữa signaling và API/trình duyệt phải để bản cũ và bản mới cùng hiểu được trong một khoảng thời gian |
| `coturn` | Image cố định, chỉ tạo lại khi định nghĩa đổi; khi đó cuộc gọi đang đi qua TURN rớt và tự nối lại (ICE). Làm ngoài giờ học |
| `caddy` | `Caddyfile` đổi: nạp lại êm. Định nghĩa service đổi (image, cổng): tạo lại, mất kết nối vài giây — làm ngoài giờ học |
| `postgres`, `backup` | Tag `konnyaku-postgres:17` không theo `IMAGE_TAG`, deploy app không đụng tới. Đổi phiên bản pgBackRest: `docker compose build --no-cache postgres` rồi `docker compose up -d postgres backup` ngoài giờ học |

Số đo (máy dev Windows + Docker Desktop **dùng chung với các phiên khác**, stack production 2 web + 2 api + worker, DB review; k6 [`infra/loadtest/deploy.js`](../infra/loadtest/deploy.js) qua Caddy suốt lúc deploy, ~7 request/s: xem trang công khai, `/ops`, `/learn`, gửi form đăng ký (POST), đăng nhập (POST)):

| Cách deploy | Thời gian | Request | Lỗi |
| --- | --- | --- | --- |
| Cũ: đổi tag + `docker compose up -d` (mọi bản sao tạo lại cùng lúc) | ~50 s | 636 | **122** (500/502/503, treo 60 s) |
| `deploy.sh` (bản cuối) | 158 s | 1 749 | **0** |
| `deploy.sh` quay lại tag trước (rollback) | 126 s | 1 524 | **0** |

Các lượt đo trung gian chỉ ra từng nguyên nhân lỗi đã sửa ở trên: chưa có drain → `UND_ERR_SOCKET` web→api và 502 cho POST khi web dừng; Caddy dựa vào DNS → POST tới IP vừa biến mất hoặc tới container chưa nghe cổng bị 502 (thử lại với thân rỗng), và một lỗi làm bản sao khoẻ bị loại 30 s (`fail_duration` cũ) → loạt 503. Lần deploy **đầu tiên** có thay đổi này vẫn có thể có vài lỗi (bản sao cũ chưa có drain); từ lần sau là 0.

### Migration tương thích ngược

Trong lúc cuốn chiếu và khi quay lại bản trước, **code bản cũ chạy trên schema mới**. Mọi migration phải để bản code liền trước vẫn chạy đúng:

- Thêm cột/bảng: được (cột mới cho phép NULL hoặc có giá trị mặc định).
- Đổi tên hoặc xoá cột/bảng: tách hai bản — bản N thêm cái mới và ghi cả hai (hoặc thôi đọc cái cũ), bản N+1 mới xoá cái cũ.
- Đổi kiểu, thêm `NOT NULL` hay ràng buộc mới lên dữ liệu đang có: thêm cột mới + chép dữ liệu, chuyển code sang, xoá cột cũ ở bản sau.
- Event (`...V1`) và API giữa các module: giữ hợp đồng cũ, thêm `V2` khi phá vỡ (quy tắc 3 trong `CLAUDE.md`).

### Quay lại bản trước (rollback)

Cũng cuốn chiếu, không gián đoạn: chạy lại script với SHA cũ (lấy từ lịch sử Actions hoặc `docker images`):

```bash
cd /opt/konnyaku && ./scripts/deploy.sh <sha cũ>
```

Migration không tự đảo ngược: bản cũ chạy trên schema mới (vì thế cần quy tắc ở trên). Nếu chính dữ liệu bị hỏng do bản lỗi, xem [Khôi phục](#khôi-phục).

## Captcha của form công khai

Form đăng ký tư vấn dùng captcha **proof-of-work tự host** (cơ chế giống ALTCHA, không gọi Google/Cloudflare, không gửi dữ liệu người dùng ra ngoài):

1. Trình duyệt gọi `GET /api/captcha` (Next.js chuyển tiếp tới `GET /api/crm/public/captcha` kèm host của trung tâm). API trả `salt` (ngẫu nhiên + thời điểm hết hạn), `challenge` = SHA-256(`salt` + số bí mật trong `[0, maxNumber]`) và `signature` = HMAC-SHA256(`CAPTCHA_KEY`, tenant + salt + challenge). API không lưu gì ở bước này.
2. Trình duyệt thử lần lượt các số tới khi ra đúng `challenge` (trung bình ~0,2–1 giây), rồi gửi số đó cùng form.
3. `POST /api/crm/public/leads` kiểm tra chữ ký (đúng trung tâm, không bị sửa), hạn dùng, lời giải; lời giải chỉ dùng được **một lần**: bảng `crm.captcha_redemption` (unique theo tenant + challenge) được ghi trong cùng transaction với lead, nên form bị từ chối vì lỗi nhập liệu không làm mất lời giải. Bản ghi hết hạn tự xóa khi có lead mới.
4. Honeypot `website` và giới hạn `RateLimits:PublicFormsPerMinute` (mặc định 10 lần/phút/IP) vẫn giữ.

| Biến (`.env`) | Cấu hình API | Mặc định | Ý nghĩa |
|---|---|---|---|
| `CAPTCHA_KEY` | `Captcha__Key` | — (bắt buộc) | Khóa HMAC ≥ 32 byte, chung cho mọi bản sao API. Đổi khóa chỉ làm hỏng các thử thách đang mở (khách bấm gửi lại là được) |
| `CAPTCHA_ENABLED` | `Captcha__Enabled` | `true` | `false` chỉ dùng cho môi trường test kín; khi tắt, `GET /api/crm/public/captcha` trả 204 và form gửi không cần lời giải |
| — | `Captcha__MaxNumber` | `100000` | Độ khó: tăng gấp đôi thì thời gian giải trên máy khách tăng gấp đôi |
| — | `Captcha__ExpiresMinutes` | `30` | Thời hạn của một thử thách |

Ở dev (`appsettings.Development.json`) captcha bật với khóa dev; test tích hợp tắt captcha trừ `CaptchaTests`; `infra/scripts/seed_review.py` tự giải thử thách như trình duyệt.

## Bảng điều khiển nền tảng và tạo trung tâm

Người vận hành Konnyaku quản lý các trung tâm tại **`https://admin.BASE_DOMAIN/platform`** (host quản trị; DNS `*.BASE_DOMAIN` đã trỏ về server, Caddy tự cấp chứng chỉ vì `tls-ask` nhận host này). Khu vực `/platform` chỉ phục vụ trên host quản trị: trên host trung tâm nó trả 404, và API từ chối mọi lệnh của bảng điều khiển gửi từ host khác. `admin` là mã được nền tảng giữ lại, không trung tâm nào dùng được.

Tài khoản nền tảng tách hẳn khỏi tài khoản trung tâm: bảng `platform.operator`, token riêng (audience `platform`, không mang `tid`), cookie riêng `kk_platform`. Đăng nhập bằng email + mật khẩu (tối thiểu 12 ký tự) + **mã TOTP bắt buộc** từ ứng dụng xác thực; 10 mã khôi phục dùng một lần được cấp khi bật TOTP; sai 5 lần (mật khẩu hoặc mã) thì khóa 15 phút. Secret TOTP được mã hoá trong DB bằng khóa suy ra từ `Platform__SecretKey` (nếu không đặt thì từ `AUTH_SIGNING_KEY`): đổi khóa này thì mọi người vận hành phải bật lại TOTP.

### Tạo người vận hành đầu tiên (một lần)

Endpoint bootstrap chỉ chạy được khi **chưa có người vận hành nào**, và cần `X-Platform-Key`. API không mở ra Internet, nên gọi từ trong mạng nội bộ của stack:

```bash
cd /opt/konnyaku && set -a && . ./.env && set +a
docker run --rm --network konnyaku_app curlimages/curl -s -X POST http://api:8080/api/platform/operators/bootstrap \
  -H "X-Platform-Key: $PLATFORM_ADMIN_KEY" -H "Content-Type: application/json" \
  -d '{"email":"ten.ban@congty.vn","displayName":"Tên bạn","password":"<mật khẩu dài, ít nhất 12 ký tự>"}'
```

Rồi mở `https://admin.BASE_DOMAIN/platform`, đăng nhập bằng email + mật khẩu đó: lần đầu trang hiện mã QR để thêm vào ứng dụng xác thực, nhập mã 6 số để xác nhận, rồi **lưu 10 mã khôi phục** (chỉ hiện một lần). Sau đó mời những người vận hành khác ở mục **Người vận hành** (mật khẩu tạm hiện một lần; họ bật TOTP và đặt mật khẩu mới ở lần đăng nhập đầu). Không gửi mật khẩu trong lệnh lên lịch sử shell dùng chung: chạy lệnh trên trong một phiên riêng hoặc xoá dòng khỏi `~/.bash_history`.

### Tạo trung tâm

Trong bảng điều khiển: **Trung tâm → Tạo trung tâm**, nhập tên, mã (tên miền con), ngôn ngữ, múi giờ và quản trị viên đầu tiên. Màn hình hiện địa chỉ `https://<mã>.BASE_DOMAIN` và mật khẩu tạm của quản trị viên (một lần). Lần đầu mở địa chỉ đó, Caddy hỏi web app host này có thuộc trung tâm nào không rồi mới xin chứng chỉ, nên lần tải đầu chậm vài giây.

Cũng trong trang của từng trung tâm: **tạm khóa / mở lại** (khi khóa, mọi đăng nhập và lệnh API của trung tâm bị từ chối với mã `platform.tenant_suspended`, trang công khai hiện thông báo tạm ngừng; mỗi bản sao API thấy thay đổi trong tối đa 1 phút vì cache tenant), **thêm tên miền riêng** (khách trỏ DNS về server trước; không nhận tên miền thuộc `BASE_DOMAIN`), **thêm quản trị viên** (khi quản trị viên cũ nghỉ). Mọi thao tác — kể cả đăng nhập, khóa tài khoản do sai nhiều lần — nằm ở **Nhật ký** (bảng `platform.audit_entry`, role `kk_platform` chỉ được thêm và đọc).

### `X-Platform-Key` chỉ cho tự động hoá

`PLATFORM_ADMIN_KEY` vẫn mở `POST /api/platform/tenants`, `POST /api/identity/bootstrap/tenant-admin` và bootstrap người vận hành, cho script, seed và CI. Khóa này không gắn với người nào (nhật ký ghi "tự động hoá"), nên không dùng cho việc vận hành hằng ngày; giữ trong `.env`, đổi định kỳ, không đưa cho người vận hành.

### Đổi host quản trị

Mặc định `admin.BASE_DOMAIN`. Muốn dùng host khác: đặt cùng một giá trị cho `Platform__AdminHost` (api, worker) và `KONNYAKU_ADMIN_HOST` (web) trong `compose.yaml`, và trỏ DNS host đó về server. Muốn hạn chế thêm theo IP (VPN văn phòng), thêm khối `@admin host admin.<domain>` + `remote_ip` trong `Caddyfile`.

Bảo mật tài khoản ở production (mặc định, không cần đặt biến):

- Quản trị và Kế toán **bắt buộc xác thực 2 bước**: lần đăng nhập đầu tiên, quản trị viên vừa tạo được hướng dẫn quét mã QR và lưu mã khôi phục. Tắt bắt buộc (không khuyến khích): `Auth__TwoFactor__Enforce=false`; đổi danh sách vai trò: `Auth__TwoFactor__RequiredRoles__0=…`.
- Khoá TOTP lưu mã hoá AES-GCM bằng khoá suy ra từ `Auth__SecretKey` (nếu không đặt thì từ `Auth__SigningKey`). Đổi khoá này làm mọi 2FA đã bật không dùng được nữa.
- Email quên mật khẩu gửi qua module Notification (cần SMTP như các email khác); link là `Notification:SiteUrlTemplate` của trung tâm + `/login/reset`. Link không được lưu lại sau khi gửi và không hiện trong nhật ký thông báo.
- Endpoint mô phỏng buổi học (`/api/workspace/dev/...`) tắt; chỉ bật cho staging bằng `Workspace__DevTools=true`.

## Kho file (Garage)

File của người dùng (tài liệu giảng dạy, media câu hỏi, bài nộp, tệp đính kèm, ảnh đại diện) do module **Content** quản lý (schema `content`, role `kk_content`). Nguyên tắc: **file không đi qua API**. Trình duyệt xin API một URL ký sẵn, `PUT` thẳng lên kho, rồi báo API kiểm tra; tải xuống cũng bằng URL `GET` ký sẵn hạn 5 phút. Không có URL công khai cố định nào. Module khác chỉ lưu `file_id` (`IContentApi`).

| Bước | Ai làm | Kiểm tra |
| --- | --- | --- |
| `POST /api/content/uploads` | API | Loại file trong danh sách cho phép (ảnh, âm thanh, PDF, Office, video ngắn; không HTML/SVG/script), giới hạn theo mục đích và vai trò, quota của trung tâm (cả phần đang tải dở). Trả URL `PUT` hạn **10 phút** + `file_id` (Pending) |
| `PUT` lên `files.BASE_DOMAIN` | Trình duyệt | Chữ ký S3 gồm cả `Content-Type`; Caddy chặn body > 110 MB |
| `POST /api/content/uploads/{id}/complete` | API | Kích thước thật, loại thật theo magic bytes, **xoá EXIF/XMP** của ảnh (giữ hướng ảnh), SHA-256, quét virus nếu bật, cộng quota → Ready. Sai thì xoá object |
| `GET /api/content/files/{id}/download` | API | Quyền đọc (chủ file, quản trị file, mục đích), trả URL `GET` hạn **5 phút** |
| Dọn dẹp | Worker | Mỗi giờ xoá các upload Pending quá 24 giờ (`Content:PendingTtlHours`) |

Giới hạn: tài liệu 100 MB, media câu hỏi và bài nộp của nhân sự 50 MB, bài nộp của học viên 25 MB, tệp đính kèm tin nhắn 10 MB, ảnh đại diện 2 MB, mọi ảnh tối đa 20 MB. Quota mặc định mỗi trung tâm `CONTENT_DEFAULT_QUOTA_BYTES` (5 GiB). Object key chỉ gồm id (`{tenant}/{mục đích}/{năm}/{tháng}/{file}`), không chứa tên file.

### Vì sao Garage

MinIO không còn image trên Docker Hub (09/2026). **Garage** (Rust, AGPL, image chỉ có một binary) có API S3 đủ cho URL ký sẵn, CORS và đọc theo khoảng byte; chạy một node với metadata SQLite tốn **~20–60 MiB** RAM, cộng tối đa 32 MiB khối đang ghi (`block_ram_buffer_max` trong `garage/garage.toml`). Trần mặc định `GARAGE_MEM_LIMIT=192m` (máy 2 GB: `128m`). Một node chỉ có một bản dữ liệu: độ bền là bản sao ngoài server ([Backup](#backup)), không phải nhân bản.

### Cài đặt

1. DNS: `files.BASE_DOMAIN` đã nằm trong bản ghi `*.BASE_DOMAIN`. Caddy cấp chứng chỉ thường (không on-demand) cho tên này.
2. `.env`: `gen-secrets.sh` sinh `GARAGE_RPC_SECRET`, `GARAGE_ADMIN_TOKEN`, `S3_ACCESS_KEY` (dạng `GK` + 24 hex), `S3_SECRET_KEY`. `GARAGE_CAPACITY` (100G) là phần đĩa Garage được dùng.
3. `./scripts/deploy.sh` chạy `scripts/garage-init.sh` sau khi bật hạ tầng: gán vai trò lưu trữ cho node (một lần), tạo bucket `S3_BUCKET`, nhập khoá ứng dụng với quyền đọc/ghi/owner trên bucket. Chạy lại bao nhiêu lần cũng được. API tự đặt luật CORS của bucket khi khởi động (`S3_CORS_ORIGIN`, mặc định `*`: mọi host tenant và tên miền riêng; chữ ký URL mới là quyền. Garage không hiểu mẫu `https://*.domain`).
4. Server có sẵn DB: tạo role `kk_content` bằng tay (script init chỉ chạy trên volume rỗng):

   ```bash
   docker compose exec -T postgres psql -U konnyaku -d konnyaku -v ON_ERROR_STOP=1 \
     -v pw="$(grep ^DB_PASSWORD_CONTENT= .env | cut -d= -f2-)" <<'SQL'
   SELECT format('CREATE ROLE kk_content LOGIN NOSUPERUSER NOBYPASSRLS PASSWORD %L', :'pw') \gexec
   CREATE SCHEMA IF NOT EXISTS content AUTHORIZATION konnyaku;
   GRANT USAGE ON SCHEMA content TO kk_content;
   ALTER DEFAULT PRIVILEGES FOR ROLE konnyaku IN SCHEMA content GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO kk_content;
   ALTER DEFAULT PRIVILEGES FOR ROLE konnyaku IN SCHEMA content GRANT USAGE, SELECT ON SEQUENCES TO kk_content;
   SQL
   ```

Kiểm tra: `docker compose exec garage /garage status`, `docker compose exec garage /garage bucket info konnyaku` (số object, dung lượng).

### Quét virus (tuỳ chọn)

Profile `clamav` (`COMPOSE_PROFILES=clamav` và `CLAMAV_HOST=clamav` trong `.env`): ClamAV giữ cơ sở dữ liệu chữ ký trong RAM (**~1–1,5 GiB**, `CLAMAV_MEM_LIMIT=1536m`), nên chỉ bật trên máy 8 GB. API gửi file cho `clamd` (INSTREAM, đọc dần, không giữ cả file) ở bước complete; file bị phát hiện chuyển sang **Quarantined**: vẫn tính vào quota, hiện trong trang **Tệp** của quản trị trung tâm, không ai mở được. ClamAV không trả lời thì complete báo `content.scan_unavailable`, file vẫn Pending để thử lại. **Không bật** (máy 2 GB): học viên chỉ tải được ảnh, PDF và âm thanh; Office và video chỉ nhân sự tải lên.

### Chuyển sang object storage của nhà cung cấp

Khi dữ liệu lớn hơn đĩa VPS (Viettel IDC, BizFly, FPT, VNG Cloud…): chỉ đổi biến môi trường, không đổi code.

1. Tạo bucket riêng (không public) và khoá chỉ có quyền trên bucket đó, có quyền đặt CORS (hoặc đặt CORS bằng tay: `GET`, `PUT`, `HEAD`, mọi header, origin `*`).
2. Chép dữ liệu khi site còn chạy, rồi chép lần cuối trong giờ nghỉ (remote `dst` khai báo bằng biến `RCLONE_CONFIG_DST_*`: `TYPE=s3`, `PROVIDER=Other`, `ENDPOINT`, `REGION`, `ACCESS_KEY_ID`, `SECRET_ACCESS_KEY`):

   ```bash
   docker compose exec -e RCLONE_CONFIG_DST_TYPE=s3 -e RCLONE_CONFIG_DST_PROVIDER=Other -e RCLONE_CONFIG_DST_ENDPOINT=https://s3.<nhà-cung-cấp> \
     -e RCLONE_CONFIG_DST_ACCESS_KEY_ID=<khoá> -e RCLONE_CONFIG_DST_SECRET_ACCESS_KEY=<bí mật> \
     files-backup /bin/sh /opt/konnyaku/files-backup.sh sync src:konnyaku dst:<bucket-mới>
   ```

3. `.env`: `S3_ENDPOINT`, `S3_PUBLIC_URL` (thường cùng URL), `S3_REGION`, `S3_BUCKET`, `S3_ACCESS_KEY`, `S3_SECRET_KEY` của nhà cung cấp, `S3_FORCE_PATH_STYLE` theo hướng dẫn của họ, `GARAGE_REPLICAS=0`, rồi `./scripts/deploy.sh`. Object key giữ nguyên nên mọi `file_id` vẫn dùng được; `files-backup` tự chép từ bucket mới.
4. Giữ volume `garage-data` vài tuần rồi mới xoá.

## Backup

Hai lớp cho cơ sở dữ liệu và một lớp cho kho file, đều chạy tự động:

| Lớp | Công cụ | Điểm khôi phục | Ở đâu |
| --- | --- | --- | --- |
| Chính | **pgBackRest** (service `backup` + `archive_command` của PostgreSQL) | **Bất kỳ thời điểm nào** trong `BACKUP_RETENTION_DAYS` (30) ngày gần nhất; mất server thì mất tối đa `PG_ARCHIVE_TIMEOUT` (60) giây dữ liệu | Object storage S3 ở nhà cung cấp khác, mã hoá phía client |
| Phụ | `scripts/backup.sh` (`pg_dump` hằng đêm qua cron) | Theo ngày, `BACKUP_KEEP_DAYS` (7) ngày | Trên server (+ `rclone` nếu đặt `RCLONE_REMOTE`) |
| Kho file | Service `files-backup` (rclone sync bucket, hằng ngày lúc `BACKUP_TIME_UTC`) | Bucket như lần chạy gần nhất; file bị xoá hoặc thay được giữ `BACKUP_RETENTION_DAYS` ngày trong `history/<ngày>/` | Cùng object storage với pgBackRest, thư mục `BACKUP_S3_PATH/files`, mã hoá tên và nội dung phía client (rclone crypt, khoá từ `BACKUP_CIPHER_PASS`) |

**Vì sao pgBackRest (không phải WAL-G)**: cùng làm được full + WAL liên tục lên S3 và PITR; pgBackRest có sẵn trong repo PGDG mà image `postgres:17` đã cấu hình (một dòng `apt-get`, cùng nguồn với PostgreSQL), có **chính sách giữ theo thời gian** (`repo1-retention-full-type=time`: giữ đủ full để khôi phục mọi thời điểm trong N ngày, WAL cũ hơn tự xoá), **mã hoá AES-256 phía client** chỉ bằng một mật khẩu (WAL-G cần quản lý khoá libsodium/PGP riêng), backup **diff/incr theo khối** (`repo1-block`) ít tải lên hơn, `info --output=json` cho file trạng thái, và `check` xác minh cả đường đẩy WAL. Đổi lại pgBackRest lớn hơn một chút và cần chạy cạnh PostgreSQL (cùng volume, cùng socket) — ở đây là sidecar `backup` dùng chung image.

**Vì sao giữ `pg_dump`**: độc lập hoàn toàn với pgBackRest (công cụ khác, định dạng logic, không cần khoá mã hoá), lấy lại được một schema hay một bảng, và đọc được bằng PostgreSQL bản khác. Tốn vài giây CPU mỗi đêm và dung lượng đĩa bằng vài lần DB đã nén. Bỏ nó khi DB lớn tới mức dump mất hàng chục phút.

### Cài đặt

1. **Object storage** ở nhà cung cấp **khác** VPS (Backblaze B2, Cloudflare R2, Wasabi, hoặc object storage của Viettel IDC, VNG Cloud, FPT, BizFly…): tạo một bucket riêng (không public), một khoá truy cập **chỉ có quyền trên bucket đó**. Nếu nhà cung cấp có object lock / versioning thì bật để chống xoá nhầm hoặc bị xoá bởi kẻ đã chiếm server (pgBackRest tự xoá bản hết hạn, nên đặt thời gian giữ của lock ngắn hơn hoặc bằng `BACKUP_RETENTION_DAYS`).
2. Điền khối `BACKUP_S3_*` trong `.env` (ví dụ B2 và R2 có trong `.env.example`). pgBackRest luôn dùng HTTPS.
3. `BACKUP_CIPHER_PASS` do `gen-secrets.sh` sinh (server cũ: thêm tay `BACKUP_CIPHER_PASS=$(openssl rand -hex 32)`). **Lưu khoá này ngoài server** — trình quản lý mật khẩu của nhóm, cùng chỗ với bản sao `.env`: mất server là mất `.env`, và không có khoá thì backup trên object storage vô dụng. Không đổi khoá khi repo đã có dữ liệu (muốn đổi: dùng `BACKUP_S3_PATH` mới và để bản cũ hết hạn).
4. `./scripts/deploy.sh` (hoặc `docker compose up -d`). Service `backup` tạo stanza, kiểm tra đường đẩy WAL (`pgbackrest check`) và chạy full backup đầu tiên ngay. Xem: `docker compose logs -f backup`, `./scripts/kk-backup.sh info`.
5. Cron cho lớp phụ:

   ```
   15 2 * * * cd /opt/konnyaku && ./scripts/backup.sh >> backups/backup.log 2>&1
   ```

Kho file dùng cùng cấu hình `BACKUP_S3_*`, không cần làm gì thêm. Chạy tay `./scripts/kk-backup.sh files backup`, xem `./scripts/kk-backup.sh files status`. DB và bucket được backup ở hai thời điểm khác nhau: một file tải lên giữa hai lần chép có trong DB khôi phục nhưng chưa có trong bucket (trang **Tệp** vẫn liệt kê, mở thì báo không tìm thấy). RAM của `files-backup`: vài MiB lúc chờ, vài chục MiB khi chép (`FILES_BACKUP_MEM_LIMIT=128m`, máy 2 GB `96m`).

Server đã chạy từ trước bản này: lần deploy đầu tạo lại `postgres` (image mới, bật `archive_mode`) — **một lần khởi động lại DB, vài giây lỗi**, làm ngoài giờ học.

### Lịch và giữ bản

| Biến | Mặc định | Ý nghĩa |
| --- | --- | --- |
| `BACKUP_TIME_UTC` | `19:30` (02:30 giờ Việt Nam) | Giờ chạy backup hằng ngày |
| `BACKUP_FULL_WEEKDAY` | `7` (Chủ nhật) | Ngày full; các ngày khác diff. Full cũng chạy khi bản full mới nhất cũ hơn 7 ngày (service ngừng đúng hôm đó) |
| `BACKUP_RETENTION_DAYS` | `30` | Khôi phục được mọi thời điểm trong N ngày; với full hằng tuần thường giữ 5 bản full + diff + WAL |
| `PG_ARCHIVE_TIMEOUT` | `60` | Khi có ghi, đoạn WAL được đóng và đẩy đi ít nhất mỗi N giây: lượng dữ liệu tối đa mất khi mất server |
| `BACKUP_DRILL_DAY` | `1` | Ngày trong tháng chạy diễn tập sau backup (`0`: tắt) |
| `BACKUP_WAL_QUEUE_MAX` | `4GiB` | Object storage không tới được lâu: quá ngưỡng này WAL bị bỏ để không đầy đĩa (PITR có khoảng trống tới full kế tiếp; cảnh báo `wal_pending_segments` báo trước) |
| `BACKUP_MEM_LIMIT` | `256m` (máy 2 GB: `128m`) | Trần RAM của service `backup` (diễn tập gồm cả một PostgreSQL tạm) |

Backup tay: `./scripts/kk-backup.sh backup full` (hoặc `diff`, `incr`).

**RAM** (đo bằng `docker stats` mỗi giây trên stack thử, DB review ~30 MB): service `backup` **~2 MiB** lúc chờ (vòng lặp shell), đỉnh **~37 MiB** khi chạy diff backup, **~42 MiB** khi diễn tập (pgBackRest restore + PostgreSQL tạm 32 MB buffer). Đẩy WAL chạy trong container `postgres` dưới dạng tiến trình ngắn của pgBackRest, không làm đổi đáng kể RAM của nó. Trần mặc định 256m / 128m để dư cho DB lớn hơn; tổng RAM thêm vào stack lúc thường gần như bằng 0.

### File trạng thái (cho giám sát)

Ghi vào `BACKUP_DIR/status/` trên host (mặc định `/opt/konnyaku/backups/status/`; trong container là `/backups/status/`), thay file nguyên khối (ghi file tạm rồi `mv`). Định dạng Prometheus text (đọc được bằng textfile collector của node_exporter) và JSON cùng nội dung:

| File | Ai ghi, khi nào | Chỉ số |
| --- | --- | --- |
| `pgbackrest.prom`, `backup.json` | Service `backup`, mỗi phút và sau mỗi backup/diễn tập | `konnyaku_backup_configured` (0/1), `konnyaku_backup_heartbeat_timestamp_seconds`, `konnyaku_backup_last_success_timestamp_seconds{type="full\|diff\|incr"}`, `konnyaku_backup_last_run_timestamp_seconds`, `konnyaku_backup_last_run_success`, `konnyaku_backup_count`, `konnyaku_backup_oldest_timestamp_seconds`, `konnyaku_backup_repo_size_bytes`, `konnyaku_backup_wal_last_archived_timestamp_seconds`, `konnyaku_backup_wal_last_failed_timestamp_seconds`, `konnyaku_backup_wal_failed_total`, `konnyaku_backup_wal_pending_segments`, `konnyaku_backup_drill_last_run_timestamp_seconds`, `konnyaku_backup_drill_last_success`, `konnyaku_backup_drill_last_success_timestamp_seconds` |
| `drill.json` | Sau mỗi lần diễn tập | thời điểm, đạt/không, mốc khôi phục, thời gian, số dòng từng bảng (khôi phục / live), ghi chú |
| `dump.prom`, `dump.json` | `scripts/backup.sh`, khi dump thành công | `konnyaku_dump_last_success_timestamp_seconds`, `konnyaku_dump_last_size_bytes` |
| `files.prom`, `files.json` | Service `files-backup`, mỗi phút và sau mỗi lần chép | `konnyaku_files_backup_configured`, `konnyaku_files_backup_heartbeat_timestamp_seconds`, `konnyaku_files_backup_last_success_timestamp_seconds`, `konnyaku_files_backup_last_run_timestamp_seconds`, `konnyaku_files_backup_last_run_success`, `konnyaku_files_backup_objects`, `konnyaku_files_backup_bytes` |

Timestamp là giây Unix UTC, `0` = chưa từng. Khi bật giám sát, node-exporter đọc thư mục này và các cảnh báo `BackupOverdue`, `DumpOverdue`, `FilesBackupOverdue`, `BackupSidecarStalled`, `WalArchivingFailing`, `BackupNotConfigured`, `RestoreDrillOverdue`, `BackupStatusMissing` dùng các chỉ số trên ([Cảnh báo](#cảnh-báo)).

`konnyaku_backup_wal_last_archived_timestamp_seconds` cũ **không** có nghĩa là lỗi: DB không có ghi thì không đóng đoạn WAL mới.

## Khôi phục

| Tình huống | Làm gì |
| --- | --- |
| Lỡ tay xoá/sửa dữ liệu, cả hệ thống cần quay lại | `./scripts/restore.sh --time "<thời điểm ngay trước sự cố>"` |
| Lỡ tay xoá dữ liệu **một trung tâm**, các trung tâm khác vẫn đang làm việc | Không quay cả DB: dựng bản tạm ở thời điểm trước sự cố rồi chép riêng dữ liệu cần lấy (bên dưới) |
| Mất server, đĩa hỏng | Server mới + `./scripts/restore.sh latest` |
| Mất file trong kho (mất server, xoá nhầm bucket) | `./scripts/kk-backup.sh files restore` ([Kho file](#kho-file)) |
| Khôi phục nhầm | `restore.sh` đã đánh dấu trạng thái trước đó: `./scripts/restore.sh --name before-restore-<thời gian> --timeline <số timeline cũ>` (script in ra cả hai khi chạy) |

Mọi lệnh chạy trong `/opt/konnyaku`. Xem trước khoảng có thể khôi phục: `./scripts/restore.sh --list` (bản full/diff, WAL từ … đến …).

### Về một thời điểm (PITR)

```bash
./scripts/restore.sh --time "2026-11-25 10:30:00+07"
```

Script: đánh dấu trạng thái hiện tại (restore point `before-restore-…`) và đẩy WAL mới nhất → dừng web/api/worker/signaling/backup rồi PostgreSQL → pgBackRest khôi phục bản full/diff phù hợp vào volume (chỉ tải file khác, `--delta`) → khởi động PostgreSQL, chờ phát lại WAL tới đúng thời điểm và mở DB (timeline mới) → bật lại mọi thứ, chạy một full backup mới. Mọi thứ ghi sau thời điểm đó **mất với mọi trung tâm** (vẫn nằm trong repo ở timeline cũ). Thời gian ≈ dừng site + tải bản full/diff + phát lại WAL + bật lại site; trên máy dev với DB review: site gián đoạn **78 s** (dừng 15 s, pgBackRest restore `--delta` 2 s, phát lại WAL và mở DB 20 s, bật lại container ~40 s), cộng 7 s full backup sau đó. Server mới (volume trống): 156 s.

### Lên server mới

1. Cài Docker, mở firewall, trỏ DNS như [Chuẩn bị](#chuẩn-bị-một-lần); chép `infra/deploy/` vào `/opt/konnyaku`.
2. Lấy **`.env` cũ** từ trình quản lý mật khẩu (cần đúng `BACKUP_*`, `BACKUP_CIPHER_PASS`, `POSTGRES_PASSWORD` và `DB_PASSWORD_*` — mật khẩu các role nằm trong DB được khôi phục — cùng `AUTH_SIGNING_KEY`, `CAPTCHA_KEY`…). Đăng nhập GHCR.
3. `./scripts/restore.sh latest` (volume trống: khôi phục đầy đủ) rồi `./scripts/deploy.sh <sha đang chạy>`.

### Kho file

```bash
./scripts/kk-backup.sh files restore               # chép bản sao mới nhất về bucket; file mới hơn trên bucket được giữ
./scripts/kk-backup.sh files restore 2026-11-24    # thêm các phiên bản mà lần chạy ngày đó đã xoá hoặc thay
```

Server mới: sau `restore.sh latest` và `deploy.sh` (Garage đã có bucket và khoá), chạy `files restore`. Lệnh không xoá gì trên bucket, chạy lại được. Tên và nội dung trên object storage đã mã hoá, chỉ đọc được bằng `BACKUP_CIPHER_PASS`. Đã kiểm thử: chép, xoá file khỏi bucket, khôi phục theo ngày lấy lại đủ file.

### Lấy lại một phần dữ liệu

```bash
./scripts/kk-backup.sh drill --time "2026-11-25 10:30:00+07" --keep
docker compose exec backup psql -h /tmp/kk-drill -p 5499 -U konnyaku konnyaku     # xem dữ liệu ở thời điểm đó
docker compose exec -T backup pg_dump -h /tmp/kk-drill -p 5499 -U konnyaku -a -t 'crm.lead' konnyaku > lead.sql
./scripts/kk-backup.sh drill-stop
```

Bản tạm chạy trong container `backup` (không mở cổng, không bao giờ ghi vào repo backup), không đụng DB đang chạy. Chép dữ liệu về bằng SQL có lọc theo `tenant_id`; **sổ buổi học chỉ được thêm bút toán**, không chèn lại dòng cũ đè lên số dư hiện tại mà ghi bút toán điều chỉnh.

### Diễn tập hằng tháng

Tự động ngày `BACKUP_DRILL_DAY` mỗi tháng, sau backup; chạy tay bất cứ lúc nào: `./scripts/kk-backup.sh drill`.

1. Đếm số dòng các bảng chính trên DB đang chạy (một câu lệnh, cùng snapshot): `billing.credit_ledger_entry`, `billing.payment`, `billing.order`, `scheduling.session`, `academic.enrollment`, `learning.attendance`, `crm.student_profile`, `crm.lead`, `identity.user`, `platform.tenant` (đổi bằng `BACKUP_DRILL_TABLES`).
2. Tạo restore point ngay sau đó, chờ WAL chứa nó lên object storage (`pgbackrest check`).
3. Khôi phục từ object storage vào thư mục tạm trong container `backup`, khởi động một PostgreSQL riêng (socket, không TCP, `archive_mode=off`, 32 MB buffer), chờ phát lại WAL tới restore point.
4. Đạt khi: khôi phục và mở DB thành công, đủ 12 schema module, đếm được mọi bảng; số dòng khác live chỉ khi có ghi chen giữa bước 1 và 2 (ghi vào `notes`), riêng sổ buổi học chỉ được bằng hoặc nhiều hơn.
5. Ghi `drill.json`, cập nhật `pgbackrest.prom`, xoá bản tạm.

Cần đĩa trống ≈ dung lượng DB. Mỗi quý nên diễn tập thêm một lần đầy đủ trên **staging hoặc server mới** bằng `restore.sh` (đo thời gian khôi phục thật, kiểm tra đăng nhập và vài màn hình).

## Giám sát và cảnh báo

Mục tiêu: biết ngay khi hệ thống hỏng hoặc chậm, và từ một request lỗi lần ra nguyên nhân (trace qua web → API → PostgreSQL, log cùng trace, lỗi kèm stack trace). Mọi phần đều **tắt mặc định**; bật bằng `.env`.

```
api ×2, worker, web ×2 ──OTLP──► alloy ──► tempo (trace)
         │                         │  └──► prometheus (metric) ──► alertmanager ──► email (SMTP), Telegram
         │                         └─ log mọi container (Docker API) ──► loki
         └── lỗi ──► Sentry / GlitchTip (SENTRY_DSN)
prometheus ◄── node-exporter (máy), cadvisor (container), postgres-exporter, caddy :2020, signaling /metrics, blackbox (health, chứng chỉ)
grafana (dashboard, Explore) ◄── prometheus, loki, tempo, alertmanager      ⟵ Caddy: https://admin.BASE_DOMAIN/grafana
```

### Bật

Thêm vào `.env` (mẫu trong `.env.example`, khối *Monitoring*), rồi `docker compose up -d`:

```bash
COMPOSE_PROFILES=monitoring
OTEL_ENDPOINT=http://alloy:4318
ALERT_EMAIL_TO=ops@congty.vn          # nhận cảnh báo; gửi qua SMTP_* như email của ứng dụng
```

- `COMPOSE_PROFILES=monitoring` chạy 11 container giám sát (RAM thêm: [toi-uu-tai-nguyen.md](toi-uu-tai-nguyen.md#bộ-giám-sát)). Máy 2 GB nên đặt bộ giám sát trên VPS riêng (cùng mục đó).
- Cổng: không mở thêm cổng nào ra Internet. Grafana `127.0.0.1:GRAFANA_LOCAL_PORT` (3000) trên server; trong mạng nội bộ: Alloy OTLP 4318/4317, Prometheus 9090, Alertmanager 9093, Loki 3100, Tempo 3200, Caddy metrics 2020, signaling `/metrics` 5100.
- `OTEL_ENDPOINT` bật trace và metric của api/worker/web. Để trống thì ứng dụng không xuất gì (dev, test, máy không bật profile).
- Lấy một phần trace khi tải lớn: `OTEL_TRACES_RATIO=0.2` (20 % request; metric vẫn đủ 100 %).
- Tắt: xoá hai dòng trên rồi `docker compose up -d --remove-orphans`; dữ liệu giám sát nằm trong volume `prometheus-data`, `loki-data`, `tempo-data`, `grafana-data` (xoá bằng `docker volume rm` nếu không cần).

### Xem dashboard

- **Qua Caddy**: `https://admin.BASE_DOMAIN/grafana/`, đăng nhập `admin` / `GRAFANA_ADMIN_PASSWORD`. Chỉ host quản trị phục vụ đường dẫn này; Grafana có giới hạn đăng nhập sai, không cho đăng ký, không cho xem ẩn danh. Muốn chặn thêm theo IP: khối `@grafana` trong `Caddyfile` + `remote_ip`.
- **Qua SSH tunnel** (không qua Internet): `ssh -L 3000:127.0.0.1:3000 <server>` rồi mở `http://localhost:3000/grafana/`. Grafana chỉ lắng nghe `127.0.0.1` của server (`GRAFANA_LOCAL_PORT`).
- Dashboard **Konnyaku → Konnyaku — Tổng quan**: số bản sao đang chạy, request/s và lỗi 5xx (Caddy và API), p95 theo endpoint, RAM/CPU từng container và so với trần, máy chủ (RAM, CPU, đĩa), kết nối PostgreSQL theo role, outbox chờ theo module và event cũ nhất, hàng đợi thông báo và email đã gửi, số người và phòng học đang mở, log lỗi.
- **Từ một request tới nguyên nhân**: Explore → Tempo → *Search* (service `konnyaku-web` hoặc `konnyaku-api`, lọc theo route, thời gian, lỗi) → một trace cho thấy trang Next.js, lời gọi `fetch` sang API, xử lý ở API và từng câu SQL (Npgsql), rồi các event outbox mà request đó phát ra (xử lý ở worker vài giây sau, cùng trace). Nút *Logs for this span* mở log của trace trong Loki; ngược lại, dòng log API có `trace_id=…` (worker: `TraceId:…`) có nút mở trace.
- Prometheus và Alertmanager không mở ra ngoài; xem cảnh báo đang bật trong Grafana (*Alerting → Alert rules*, nguồn Prometheus) hoặc `docker compose exec prometheus wget -qO- localhost:9090/api/v1/alerts`.

Dữ liệu cá nhân: span không có query string, IP, user agent; câu SQL chỉ có tham số (`$1`), không có giá trị; event lỗi gửi Sentry bị xoá cookie, header, query, body, thông tin người dùng, và che email / số điện thoại / token / mật khẩu trong thông điệp. Alloy xoá thêm một lần các thuộc tính đó trước khi lưu. Log ứng dụng vốn không ghi dữ liệu cá nhân.

Giữ lại: metric `PROMETHEUS_RETENTION` (15 ngày, tối đa `PROMETHEUS_RETENTION_SIZE`), log `LOKI_RETENTION` (7 ngày), trace `TEMPO_RETENTION` (3 ngày).

### Cảnh báo

Rule trong `monitoring/prometheus/rules.yml.tmpl` (số bản sao và ngưỡng lấy từ `.env` khi container khởi động). **critical** gửi lại mỗi 4 giờ khi chưa hết, **warning** mỗi 24 giờ; khi hết lỗi có thư "RESOLVED". Cảnh báo critical của một service ẩn các warning cùng service.

| Cảnh báo | Mức | Khi nào |
| --- | --- | --- |
| `ServiceDown` | critical | Health check nội bộ của api / web / signaling lỗi 1 phút (mọi bản sao hỏng) |
| `PublicSiteDown` | critical | `PROBE_PUBLIC_URL` (mặc định `https://admin.BASE_DOMAIN/api/health`, qua DNS + Caddy) lỗi 2 phút |
| `ApiReplicaMissing`, `WebReplicaMissing`, `WorkerMissing` | critical | Số container đang chạy ít hơn `API_REPLICAS` / `WEB_REPLICAS` / `WORKER_REPLICAS` trong 2 phút (5 phút với worker) |
| `ContainerRestarting` | warning | Một service khởi động lại > 2 lần trong 15 phút (thường do chạm trần RAM) |
| `PostgresDown` | critical | postgres-exporter không kết nối được |
| `ApiHigh5xxRate`, `SiteHigh5xxRate` | critical | > 5 % request lỗi 5xx trong 5 phút (API; toàn trang đo ở Caddy) |
| `ApiSlowP95`, `SiteSlowP95` | warning | p95 > `ALERT_API_P95_SECONDS` (1,5 s) / `ALERT_SITE_P95_SECONDS` (3 s) trong 10 phút |
| `ContainerMemoryNearLimit` | warning | Container dùng > 90 % trần RAM 10 phút |
| `PostgresConnectionsHigh` | warning | > 85 % `max_connections` |
| `OutboxBacklog` | critical | Event cũ nhất trong outbox một module chờ > 5 phút (worker dừng, handler lỗi liên tục) |
| `OutboxDeadMessages` | warning | Có event đã hết 10 lần thử (cần người xử lý) |
| `NotificationsOverdue` | warning | Việc trong hàng đợi thông báo trễ > 15 phút |
| `ClassroomPresenceDropped` | warning | Signaling bỏ sự kiện vào/ra phòng sau khi thử lại (điểm danh có thể thiếu) |
| `DiskAlmostFull` / `DiskFillsWithin24h` | critical / warning | Ổ `/` còn < 10 % / sẽ đầy trong 24 giờ |
| `BackupOverdue`, `DumpOverdue` | critical / warning | Backup đầy đủ/khác biệt (pgBackRest) / pg_dump thành công gần nhất cũ hơn `BACKUP_MAX_AGE_HOURS` (26) |
| `FilesBackupOverdue` | critical | Bản sao kho file (service `files-backup`) thành công gần nhất cũ hơn `BACKUP_MAX_AGE_HOURS` (chỉ khi đã cấu hình `BACKUP_S3_BUCKET`) |
| `BackupSidecarStalled`, `WalArchivingFailing` | critical | Container backup không cập nhật trạng thái 5 phút / WAL không được đẩy lên kho backup 10 phút |
| `BackupNotConfigured`, `RestoreDrillOverdue`, `BackupStatusMissing` | warning | Chưa có kho backup / diễn tập khôi phục thành công cũ hơn 35 ngày / không có file trạng thái backup nào trong 26 giờ |
| `CertificateExpiresSoon` / `VerySoon` | warning / critical | Chứng chỉ của `PROBE_PUBLIC_URL` hết hạn sau < 14 / < 3 ngày (Caddy tự gia hạn trước 30 ngày) |
| `MonitoringTargetDown` | warning | Không thu được số liệu từ một exporter 5 phút |

#### Khi nhận cảnh báo

Mỗi thư có `summary` (chuyện gì) và `description` (lệnh nên chạy đầu tiên). Các bước chung, trong `/opt/konnyaku`:

| Cảnh báo | Làm gì |
| --- | --- |
| `ServiceDown`, `*ReplicaMissing`, `ContainerRestarting` | `docker compose ps` (container nào `exited`/`restarting`/`unhealthy`), `docker compose logs --tail 200 <service>`. Hết RAM (`OOMKilled` trong `docker inspect`) → xem `ContainerMemoryNearLimit` và [toi-uu-tai-nguyen.md](toi-uu-tai-nguyen.md) trước khi nâng trần. Lỗi sau deploy → quay lại bản trước (mục [Deploy](#deploy)). Không rõ lỗi → `docker compose up -d <service>` rồi theo dõi |
| `PublicSiteDown` khi `ServiceDown` không báo | Caddy hoặc DNS/chứng chỉ: `docker compose logs --tail 200 caddy`, `dig admin.BASE_DOMAIN`, thử `curl -I https://admin.BASE_DOMAIN/api/health` từ máy khác |
| `PostgresDown`, `PostgresConnectionsHigh` | `docker compose logs postgres`; đĩa đầy là nguyên nhân thường gặp. Nhiều kết nối → công thức pool ở [toi-uu-tai-nguyen.md](toi-uu-tai-nguyen.md) mục 1 |
| `ApiHigh5xxRate`, `SiteHigh5xxRate` | Dashboard → *Lỗi 5xx theo endpoint*; Grafana Explore → Tempo, lọc `status = error` để xem trace lỗi; issue mới trong Sentry/GlitchTip (có `trace_id`). 502/503 ở Caddy mà API không lỗi → web hoặc api đang khởi động lại |
| `ApiSlowP95`, `SiteSlowP95` | Dashboard → *p95 theo endpoint*; mở trace chậm trong Tempo, xem câu SQL nào lâu; CPU web gần 100 % → thêm vCPU / bản sao web |
| `OutboxBacklog`, `OutboxDeadMessages`, `NotificationsOverdue` | `docker compose logs --tail 200 worker` (máy 2 GB: `api`), tìm `Outbox message … failed`. Event hết lượt thử: đọc `last_error` (câu SQL trong thư), sửa nguyên nhân rồi đặt lại `attempts = 0` cho dòng đó để worker chạy lại |
| `DiskAlmostFull`, `DiskFillsWithin24h` | `df -h`, `docker system df`; dọn `docker image prune -a`, backup cũ, giảm `PROMETHEUS_RETENTION` / `LOKI_RETENTION` / `TEMPO_RETENTION` |
| `Backup*`, `WalArchivingFailing`, `DumpOverdue` | Mục [Backup](#backup); WAL lỗi kéo dài làm đầy đĩa PostgreSQL, xử lý trong ngày |
| `Certificate*` | `docker compose logs caddy \| grep -i acme`; cổng 80 phải mở và DNS đúng để gia hạn HTTP-01 |
| `ClassroomPresenceDropped` | `docker compose logs signaling`; buổi liên quan có thể thiếu điểm danh → giáo viên xác nhận điểm danh tay |
| `MonitoringTargetDown` | `docker compose --profile monitoring ps`; khởi động lại exporter đó |

Tạm im một cảnh báo đã biết (bảo trì): `docker compose exec alertmanager amtool silence add alertname=<tên> --duration=2h --comment="bảo trì" --alertmanager.url=http://localhost:9093`.

**Trạng thái backup**: node-exporter đọc các file `*.prom` trong `METRICS_TEXTFILE_DIR` (mặc định `BACKUP_DIR/status`), nơi các job backup ghi `konnyaku_backup_*` và `konnyaku_dump_*` (tên và ý nghĩa ở mục [Backup](#backup)). Chưa có file nào thì `BackupStatusMissing` báo sau 26 giờ.

**Kênh gửi**:

- **Email**: dùng `SMTP_*` của ứng dụng; người nhận `ALERT_EMAIL_TO`, người gửi `ALERT_EMAIL_FROM` (mặc định `SMTP_FROM_ADDRESS`). Tiêu đề `[Konnyaku <ALERT_ENVIRONMENT>] FIRING <tên cảnh báo> <service>`.
- **Telegram** (tuỳ chọn): tạo bot với @BotFather, thêm bot vào nhóm vận hành, lấy chat id (`https://api.telegram.org/bot<token>/getUpdates` sau khi nhắn một tin trong nhóm; nhóm là số âm), đặt `TELEGRAM_BOT_TOKEN` và `TELEGRAM_CHAT_ID`.
- **Thử**: `docker compose stop api` → sau ~2–3 phút có thư `ApiReplicaMissing` (và `ServiceDown`); `docker compose start api` → thư RESOLVED. Đổi ngưỡng hoặc thêm rule: sửa `rules.yml.tmpl` rồi `docker compose restart prometheus`.

### Theo dõi từ bên ngoài (uptime)

Bộ giám sát chạy trên chính server nên không báo được khi **cả server** mất điện hay mất mạng. Thêm một kiểm tra từ bên ngoài, ở nơi khác:

- **Dịch vụ miễn phí**: UptimeRobot (50 monitor, mỗi 5 phút), Better Stack, Healthchecks.io… Tạo monitor HTTP tới `https://admin.BASE_DOMAIN/api/health` (trả 200 khi web chạy) và một monitor cho trang của một trung tâm thật; bật cảnh báo email/Telegram và kiểm tra hạn chứng chỉ nếu dịch vụ có.
- **Uptime Kuma tự host** trên một máy khác (VPS nhỏ nhất, hoặc chung với bộ giám sát tách riêng): `docker run -d --restart=unless-stopped -p 127.0.0.1:3001:3001 -v uptime-kuma:/app/data louislam/uptime-kuma:1`, mở qua SSH tunnel, thêm monitor HTTP(s) như trên (60 giây) và *Certificate Expiry Notification*.
- Có thể dùng chính monitor đó làm trang trạng thái công khai cho khách hàng.

### Bắt lỗi ứng dụng (Sentry / GlitchTip)

Đặt `SENTRY_DSN` (và tuỳ chọn `SENTRY_WEB_DSN` cho một project riêng của web), rồi `docker compose up -d`. API và worker gửi lỗi chưa bắt và mọi log mức Error (outbox lỗi, job lỗi) kèm stack trace; web gửi lỗi khi render trang, route handler và server action (chỉ phía server; lỗi trong trình duyệt chưa gửi). Mỗi lỗi có thẻ `trace_id` để mở trace trong Grafana, phiên bản (`IMAGE_TAG`) và môi trường (`SENTRY_ENVIRONMENT`).

- **Sentry** (sentry.io): gói miễn phí đủ cho pilot; tạo project ASP.NET Core (và Next.js), lấy DSN. Dữ liệu ra nước ngoài nhưng đã lọc dữ liệu cá nhân như trên.
- **GlitchTip** (tự host, tương thích SDK Sentry): cần PostgreSQL + Redis + ~512 MB RAM, nên chạy trên VPS giám sát riêng chứ không trên máy ứng dụng; tạo project, lấy DSN, dùng như Sentry.

## Xử lý dữ liệu cá nhân

Quy tắc đầy đủ: [xu-ly-du-lieu-ca-nhan.md](xu-ly-du-lieu-ca-nhan.md). Việc của người triển khai:

- Script init tạo extension `unaccent` và hàm `public.kk_unaccent` (tìm kiếm không dấu). Server đã chạy từ trước: migration Crm tự tạo khi deploy (role chủ schema `konnyaku` cần quyền tạo extension, là superuser của image postgres).
- Tuỳ chỉnh (biến môi trường cho `api` và `worker` trong `compose.yaml`, mặc định trong ngoặc): `Privacy__ExportMaxRows` (2000), `Privacy__ExportsPerDay` (20), `Privacy__LeadRetentionMonths` (24, `0` để tắt ẩn danh lead tự động), `Privacy__RequestDueHours` (72).
- Worker chạy job lưu giữ mỗi ngày; log chỉ ghi số lead đã ẩn danh theo trung tâm.
- Không bật access log của Caddy hay request log của API (URL danh sách có từ khoá tìm kiếm: tên, số điện thoại). Không bật `Include Error Detail` trong chuỗi kết nối.
- Backup (`scripts/backup.sh`) chứa dữ liệu cá nhân: giữ quyền truy cập thư mục backup như với database; dữ liệu đã ẩn danh hoá vẫn còn trong backup cũ đến khi backup đó hết hạn lưu.
- Thoả thuận xử lý dữ liệu với nhà cung cấp VPS/backup và đánh giá tác động khi chuyển dữ liệu ra nước ngoài là việc của trung tâm/nhà vận hành, ngoài phạm vi code.

## Email (SMTP)

Worker gửi email (nhắc lịch, đổi giờ / hủy buổi, xác nhận vào lớp, sắp hết buổi, lead mới, chào mừng, chốt kỳ lương) qua một máy chủ SMTP. Dùng dịch vụ email giao dịch (Amazon SES, Postmark, Mailgun, SendGrid, Brevo…), không dùng hộp thư cá nhân.

1. Tạo tên miền gửi trên nhà cung cấp, ví dụ `mail.konnyaku.example.com`, và địa chỉ gửi `no-reply@mail.konnyaku.example.com`.
2. Thêm bản ghi DNS nhà cung cấp đưa ra:
   - **SPF**: TXT trên tên miền gửi, ví dụ `v=spf1 include:<miền của nhà cung cấp> -all` (một bản ghi SPF duy nhất mỗi tên miền).
   - **DKIM**: CNAME hoặc TXT `<selector>._domainkey…` để nhà cung cấp ký thư.
   - **DMARC**: TXT `_dmarc.<miền>` = `v=DMARC1; p=none; rua=mailto:dmarc@<miền của bạn>`; khi báo cáo ổn định thì nâng `p=quarantine`.
   - Return-Path / MAIL FROM riêng nếu nhà cung cấp hỗ trợ (căn chỉnh SPF với DMARC).
3. Điền vào `.env`: `SMTP_HOST`, `SMTP_PORT` (587 + `SMTP_SECURITY=StartTls`, hoặc 465 + `SslOnConnect`), `SMTP_USERNAME`, `SMTP_PASSWORD`, `SMTP_FROM_ADDRESS`, rồi `docker compose up -d`. Tên người gửi hiển thị là tên trung tâm.
4. Thử: đổi giờ một buổi của trung tâm thử nghiệm, xem `/ops/notifications`; kiểm tra tiêu đề `Authentication-Results` của thư nhận được có `spf=pass dkim=pass dmarc=pass`.

Hành vi: mỗi thông báo một dòng trong `notification.notification_message` (khóa chống trùng theo tenant). Lỗi tạm thời (mạng, 4xx, chưa cấu hình SMTP) thử lại sau 1, 4, 16, 64, 256 phút (`Notification__MaxAttempts`, `Notification__RetryBaseDelaySeconds`); lỗi 5xx từ chối người nhận/nội dung dừng ngay và ghi lý do. Quản trị trung tâm xem và gửi lại ở `/ops/notifications`. Để trống `SMTP_HOST` thì email nằm chờ trong hàng đợi (và thành lỗi sau hết số lần thử).

Server đang chạy từ trước khi có module Notification: tạo role bằng tay vì script init chỉ chạy trên volume rỗng (thay mật khẩu bằng `DB_PASSWORD_NOTIFICATION` đã thêm vào `.env`):

```bash
docker compose exec postgres psql -U konnyaku -c "CREATE ROLE kk_notification LOGIN NOSUPERUSER NOBYPASSRLS PASSWORD '<mật khẩu>';" \
  -c "CREATE SCHEMA IF NOT EXISTS notification AUTHORIZATION konnyaku;" -c "GRANT USAGE ON SCHEMA notification TO kk_notification;" \
  -c "ALTER DEFAULT PRIVILEGES FOR ROLE konnyaku IN SCHEMA notification GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO kk_notification;" \
  -c "ALTER DEFAULT PRIVILEGES FOR ROLE konnyaku IN SCHEMA notification GRANT USAGE, SELECT ON SEQUENCES TO kk_notification;"
```

## Vận hành hằng ngày

| Việc | Lệnh (trong `/opt/konnyaku`) |
| --- | --- |
| Trạng thái | `docker compose ps` |
| Log một thành phần | `docker compose logs -f --tail 200 api` |
| Tăng bản sao web | `WEB_REPLICAS=3` trong `.env`, rồi `docker compose up -d` |
| Đổi hồ sơ RAM (2 GB / 8 GB) | Sửa khối *Machine profile* trong `.env` theo `.env.example`, rồi `docker compose up -d` ([toi-uu-tai-nguyen.md](toi-uu-tai-nguyen.md)) |
| RAM/CPU từng container | `docker stats --no-stream` |
| Vào psql | `docker compose exec postgres psql -U konnyaku` |
| Dung lượng | `docker system df` |
| Deploy / quay lại bản trước | `./scripts/deploy.sh <sha>` ([Deploy](#deploy)) |
| Backup: danh sách, khoảng khôi phục được | `./scripts/kk-backup.sh info`; trạng thái: `cat backups/status/backup.json` |
| Backup ngay | `./scripts/kk-backup.sh backup full` |
| Diễn tập khôi phục | `./scripts/kk-backup.sh drill` (kết quả: `backups/status/drill.json`) |
| Khôi phục | `./scripts/restore.sh --time "<thời điểm>"` hoặc `latest` ([Khôi phục](#khôi-phục)) |
| Phòng học đang mở, tab đang nối realtime | `docker compose exec caddy wget -qO- http://signaling:5100/rtc/health` (số phòng, số người, `realtime.connections`, `realtime.redis`); dashboard Grafana khi bật giám sát |
| Dashboard, trace, log | `https://admin.BASE_DOMAIN/grafana/` hoặc SSH tunnel ([Giám sát và cảnh báo](#giám-sát-và-cảnh-báo)) |
| Redis | `docker compose exec redis sh -c 'REDISCLI_AUTH="$REDIS_PASSWORD" redis-cli info memory'` |
| Kiểm tra TURN | Mở <https://webrtc.github.io/samples/src/content/peerconnection/trickle-ice/>, nhập `turn:BASE_DOMAIN:3478` với username/credential lấy từ một vé phòng học; phải thấy candidate `relay` |
