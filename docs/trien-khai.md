# Triển khai Konnyaku lên VPS

Pilot chạy trên **một VPS** bằng Docker Compose. Toàn bộ định nghĩa nằm trong `infra/deploy/`. Image do GitHub Actions build và đẩy lên GHCR (`.github/workflows/deploy.yml`), cho cả **amd64** và **arm64**: cùng một tag, Docker trên server tự kéo bản đúng với CPU.

## Kiến trúc

```
Internet ─80/443─► Caddy ──► web ×2 (Next.js: trang công khai, /learn, /teach, /ops, /room; /platform trên admin.BASE_DOMAIN)
                    │   │       │  (mạng app)
                    │   │       ▼
                    │   │    api ×2 (ASP.NET Core) ──┐
                    │   │       ▲                    ├─► PostgreSQL 17 (mạng data, internal)
                    │   │       │ vào/ra phòng       │
                    │   └─/rtc/*─► signaling ×1 (Rust, WebSocket)
                    │        worker ×1 ──────────────┘
                    │        migrator (chạy mỗi lần up, xong thì thoát)
                    │        backup (pgBackRest) ── WAL + backup, mã hóa ──► object storage S3 ở nhà cung cấp khác
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
| `signaling` | **1** | Phòng học có sẵn (`services/signaling`, Rust): WebSocket `/rtc/ws` qua Caddy, chỉ nhận vé ngắn hạn do API ký (`SIGNALING_SECRET`), giữ ai đang ở phòng nào **trong bộ nhớ** nên chỉ chạy một bản. Báo vào/ra/kết thúc về API qua mạng nội bộ |
| `coturn` | 1 | TURN cho người dùng sau NAT/tường lửa chặt. Dùng mạng host (`network_mode: host`). Mật khẩu tạm theo TURN REST API (`TURN_SECRET`, hạn 6 giờ); không chuyển tiếp tới địa chỉ nội bộ/loopback |

Giới hạn khi chạy nhiều bản sao: giới hạn tần suất form đăng ký công khai được đếm **riêng từng bản sao API** (2 bản sao ≈ gấp đôi hạn mức). Khi cần chính xác, chuyển bộ đếm sang Redis.

### Phòng học có sẵn (WebRTC mesh)

Thay cho Google Meet khi trung tâm chưa có Google Workspace. Trung tâm chọn bằng `PUT /api/workspace/connection` với `{"provider":"Konnyaku"}` (không cần thêm host account; đổi từ `Fake` sang được, buổi đã tạo phòng giữ link cũ).

- **Kiến trúc**: P2P dạng mesh, không SFU — mỗi người gửi hình/tiếng tới từng người khác. Ổn khoảng **4 người bật camera**; mặc định `ROOM_CAPACITY=6` (gồm giáo viên, luôn giữ một chỗ cho giáo viên), trình duyệt tự giảm bitrate khi phòng đông. Cần lớp đông hơn thì phải thêm SFU (việc sau).
- **Vào phòng**: link **Vào lớp** → API kiểm quyền → `/room/{mã}`. Trang phòng xin **vé** (`POST /api/workspace/rooms/{mã}/ticket`, JWT HS256 hạn 2 phút: tenant, buổi, người, vai trò, giờ kết thúc buổi) mỗi lần kết nối và kết nối lại; signaling chỉ nhận vé này.
- **Điểm danh, bảng công**: signaling gửi vào/ra/kết thúc tới `POST /api/workspace/internal/rooms/events` (mạng nội bộ, header `X-Rtc-Token` ký bằng `SIGNALING_SECRET`, thử lại khi API lỗi). Khi phòng kết thúc, Workspace phát `ParticipantsCollectedV1` như mọi provider → Learning tạo điểm danh nháp, Hr tạo bảng công. Phòng tự đóng khi trống sau giờ kết thúc, hoặc sau `ROOM_GRACE_MINUTES` (30) phút.
- **Khởi động lại signaling** (deploy) ngắt mọi phòng vài giây; trình duyệt tự kết nối lại, thời gian có mặt không bị tính trùng.
- **Chưa có**: ghi hình (repo chưa có object storage), TURN qua TLS cổng 443 (mạng chỉ cho phép HTTPS sẽ không kết nối được), SFU cho lớp đông, nhiều bản signaling.

## Chuẩn bị (một lần)

1. **VPS**: Ubuntu 24.04, 4 vCPU / 8 GB RAM / 100 GB SSD, x86 hoặc ARM (ví dụ Oracle Cloud Always Free, Ampere A1). Máy test 2 GB RAM (1–2 vCPU) chạy được với hồ sơ **máy nhỏ** trong `.env.example` (1 web, 1 api chạy luôn outbox, không có container worker); số đo và cách chọn tham số ở [toi-uu-tai-nguyen.md](toi-uu-tai-nguyen.md). Cài Docker Engine và plugin compose theo hướng dẫn chính thức của Docker. Mở firewall (cả firewall của nhà cung cấp cloud, ví dụ Security List của Oracle):

   | Cổng | Giao thức | Dùng cho |
   | --- | --- | --- |
   | 22 | TCP | SSH |
   | 80, 443 | TCP | Web, cấp chứng chỉ, signaling phòng học (`/rtc/*`) |
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

   Sửa trong `.env`: `BASE_DOMAIN`, `ACME_EMAIL`, `IMAGE_*` (thay `OWNER` bằng tên GitHub org/user viết **chữ thường**, ví dụ `ghcr.io/cdv00/konnyaku-web`). Server đã chạy từ trước bản có phòng học: thêm `SIGNALING_SECRET` và `TURN_SECRET` (chạy lại `gen-secrets.sh` rồi chỉ chép hai dòng đó) và `IMAGE_SIGNALING`. Lưu một bản `.env` vào trình quản lý mật khẩu của nhóm: mất file này là mất mật khẩu DB và khóa ký JWT. `gen-secrets.sh` cũng sinh `CAPTCHA_KEY` (xem mục [Captcha của form công khai](#captcha-của-form-công-khai)); `.env` có sẵn từ trước thì thêm tay một dòng `CAPTCHA_KEY=$(openssl rand -hex 32)`, nếu không `docker compose up` báo thiếu biến. Tương tự `BACKUP_CIPHER_PASS` (khoá mã hoá backup, xem [Backup](#backup)) — khoá này phải có một bản **ngoài server**.

5. **GitHub**: Settings → Environments, tạo `staging` và `production`. Mỗi environment có secrets `DEPLOY_HOST`, `DEPLOY_USER`, `DEPLOY_SSH_KEY` (khóa riêng của một user chỉ dùng để deploy, thuộc nhóm `docker`), `DEPLOY_KNOWN_HOSTS` (kết quả `ssh-keyscan -H <host>`), và biến `DEPLOY_PATH` nếu khác `/opt/konnyaku`. Nên bật *Required reviewers* cho `production`.

## Deploy

- Mỗi PR: CI build 5 image (api, worker, migrator, web, signaling) trên máy amd64 và máy arm64 thật của GitHub rồi chạy thử (`infra/deploy/scripts/smoke-test.sh`, kể cả web dừng êm khi nhận SIGTERM), build image PostgreSQL + pgBackRest và kiểm tra cú pháp các script, không đẩy lên registry.
- Merge vào `main` → CI xanh → workflow **Deploy** build lại trên từng kiến trúc, chạy thử, đẩy `<sha>-amd64` / `<sha>-arm64`, gộp thành tag đa kiến trúc `<sha>` và `latest`, rồi deploy lên **staging**.
- Lên **production**: Actions → Deploy → Run workflow → `production`.
- Workflow chép `compose.yaml`, `Caddyfile`, `caddy/`, `postgres/`, `scripts/` lên server rồi chạy **`./scripts/deploy.sh <sha>`** (deploy cuốn chiếu, bên dưới). Deploy tay trên server cũng chỉ là lệnh đó.

### Deploy cuốn chiếu (không gián đoạn)

`scripts/deploy.sh <tag>` ghi `IMAGE_TAG=<tag>` vào `.env` rồi:

1. Kéo image của tag; build `konnyaku-postgres:17` nếu server chưa có.
2. Hạ tầng (`postgres`, `backup`, `caddy`, `coturn`) chỉ được tạo lại khi định nghĩa của nó trong `compose.yaml` đổi (với `postgres` là một lần khởi động lại DB, vài giây lỗi — báo trước khi deploy bản như vậy). `Caddyfile` được nạp lại êm (`caddy reload`, không cắt kết nối).
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

## Backup

Hai lớp, cả hai chạy tự động:

| Lớp | Công cụ | Điểm khôi phục | Ở đâu |
| --- | --- | --- | --- |
| Chính | **pgBackRest** (service `backup` + `archive_command` của PostgreSQL) | **Bất kỳ thời điểm nào** trong `BACKUP_RETENTION_DAYS` (30) ngày gần nhất; mất server thì mất tối đa `PG_ARCHIVE_TIMEOUT` (60) giây dữ liệu | Object storage S3 ở nhà cung cấp khác, mã hoá phía client |
| Phụ | `scripts/backup.sh` (`pg_dump` hằng đêm qua cron) | Theo ngày, `BACKUP_KEEP_DAYS` (7) ngày | Trên server (+ `rclone` nếu đặt `RCLONE_REMOTE`) |

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

Timestamp là giây Unix UTC, `0` = chưa từng. Ngưỡng cảnh báo đề xuất:

| Cảnh báo | Điều kiện |
| --- | --- |
| Backup quá hạn | `time() - max(konnyaku_backup_last_success_timestamp_seconds) > 26 giờ`, hoặc `konnyaku_backup_configured == 0` |
| Đẩy WAL lỗi | `konnyaku_backup_wal_pending_segments > 10`, hoặc `konnyaku_backup_wal_last_failed_timestamp_seconds > konnyaku_backup_wal_last_archived_timestamp_seconds` kéo dài 10 phút |
| Service backup chết | `time() - konnyaku_backup_heartbeat_timestamp_seconds > 5 phút` (hoặc file không còn được cập nhật) |
| Diễn tập | Lần gần nhất trượt (`konnyaku_backup_drill_last_run_timestamp_seconds > 0` và `konnyaku_backup_drill_last_success == 0`), hoặc `time() - konnyaku_backup_drill_last_success_timestamp_seconds > 35 ngày` (sau tháng đầu) |
| Dump đêm | `time() - konnyaku_dump_last_success_timestamp_seconds > 26 giờ` |

`konnyaku_backup_wal_last_archived_timestamp_seconds` cũ **không** có nghĩa là lỗi: DB không có ghi thì không đóng đoạn WAL mới.

## Khôi phục

| Tình huống | Làm gì |
| --- | --- |
| Lỡ tay xoá/sửa dữ liệu, cả hệ thống cần quay lại | `./scripts/restore.sh --time "<thời điểm ngay trước sự cố>"` |
| Lỡ tay xoá dữ liệu **một trung tâm**, các trung tâm khác vẫn đang làm việc | Không quay cả DB: dựng bản tạm ở thời điểm trước sự cố rồi chép riêng dữ liệu cần lấy (bên dưới) |
| Mất server, đĩa hỏng | Server mới + `./scripts/restore.sh latest` |
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
| Phòng học đang mở | `docker compose exec caddy wget -qO- http://signaling:5100/rtc/health` (số phòng, số người) |
| Kiểm tra TURN | Mở <https://webrtc.github.io/samples/src/content/peerconnection/trickle-ice/>, nhập `turn:BASE_DOMAIN:3478` với username/credential lấy từ một vé phòng học; phải thấy candidate `relay` |
