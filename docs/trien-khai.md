# Triển khai Konnyaku lên VPS

Pilot chạy trên **một VPS** bằng Docker Compose. Toàn bộ định nghĩa nằm trong `infra/deploy/`. Image do GitHub Actions build và đẩy lên GHCR (`.github/workflows/deploy.yml`), cho cả **amd64** và **arm64**: cùng một tag, Docker trên server tự kéo bản đúng với CPU.

## Kiến trúc

```
Internet ─80/443─► Caddy ──► web ×2 (Next.js: trang công khai, /learn, /teach, /ops, /room)
                    │   │       │  (mạng app)
                    │   │       ▼
                    │   │    api ×2 (ASP.NET Core) ──┐
                    │   │       ▲                    ├─► PostgreSQL 17 (mạng data, internal)
                    │   │       │ vào/ra phòng       │
                    │   └─/rtc/*─► signaling ×1 (Rust, WebSocket)
                    │        worker ×1 ──────────────┘
                    │        migrator (chạy mỗi lần up, xong thì thoát)
                    └─ chứng chỉ HTTPS cấp tự động cho từng host tenant
Internet ─3478 + dải UDP─► coturn (TURN, mạng host)
Trình duyệt ◄────── hình/tiếng trực tiếp giữa các máy (WebRTC), qua coturn khi mạng chặn ──────► Trình duyệt
```

| Thành phần | Bản sao | Ghi chú |
| --- | --- | --- |
| `caddy` | 1 | Cổng duy nhất mở ra Internet. HTTPS tự động (on-demand) cho `{slug}.BASE_DOMAIN`; chia tải round-robin giữa các bản sao `web`, bỏ qua bản sao đang khởi động lại |
| `web` | `WEB_REPLICAS` (2) | Không lưu trạng thái. Gọi API qua mạng nội bộ, chuyển IP khách trong `X-Forwarded-For` |
| `api` | `API_REPLICAS` (2) | Không mở ra Internet. Chỉ tin `X-Forwarded-For` từ mạng `app` (`APP_SUBNET`). Cache tenant trong bộ nhớ 1 phút |
| `worker` | `WORKER_REPLICAS` (1) | Chạy nhiều bản an toàn: outbox lấy việc bằng `FOR UPDATE SKIP LOCKED` |
| `migrator` | — | Áp migration bằng role chủ schema `konnyaku`; `api`/`worker` chỉ khởi động khi migrator thành công |
| `postgres` | 1 | Mạng `data` không có đường ra Internet. Mỗi module một role `kk_<module>` với mật khẩu riêng |
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

1. **VPS**: Ubuntu 24.04, 4 vCPU / 8 GB RAM / 100 GB SSD, x86 hoặc ARM (ví dụ Oracle Cloud Always Free, Ampere A1). Máy test 2 GB RAM chạy được nếu đặt `WEB_REPLICAS=1` và `API_REPLICAS=1`. Cài Docker Engine và plugin compose theo hướng dẫn chính thức của Docker. Mở firewall (cả firewall của nhà cung cấp cloud, ví dụ Security List của Oracle):

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

   Sửa trong `.env`: `BASE_DOMAIN`, `ACME_EMAIL`, `IMAGE_*` (thay `OWNER` bằng tên GitHub org/user viết **chữ thường**, ví dụ `ghcr.io/cdv00/konnyaku-web`). Server đã chạy từ trước bản có phòng học: thêm `SIGNALING_SECRET` và `TURN_SECRET` (chạy lại `gen-secrets.sh` rồi chỉ chép hai dòng đó) và `IMAGE_SIGNALING`. Lưu một bản `.env` vào trình quản lý mật khẩu của nhóm: mất file này là mất mật khẩu DB và khóa ký JWT.

5. **GitHub**: Settings → Environments, tạo `staging` và `production`. Mỗi environment có secrets `DEPLOY_HOST`, `DEPLOY_USER`, `DEPLOY_SSH_KEY` (khóa riêng của một user chỉ dùng để deploy, thuộc nhóm `docker`), `DEPLOY_KNOWN_HOSTS` (kết quả `ssh-keyscan -H <host>`), và biến `DEPLOY_PATH` nếu khác `/opt/konnyaku`. Nên bật *Required reviewers* cho `production`.

## Deploy

- Mỗi PR: CI build 5 image (api, worker, migrator, web, signaling) trên máy amd64 và máy arm64 thật của GitHub rồi chạy thử (`infra/deploy/scripts/smoke-test.sh`), không đẩy lên registry.
- Merge vào `main` → CI xanh → workflow **Deploy** build lại trên từng kiến trúc, chạy thử, đẩy `<sha>-amd64` / `<sha>-arm64`, gộp thành tag đa kiến trúc `<sha>` và `latest`, rồi deploy lên **staging**.
- Lên **production**: Actions → Deploy → Run workflow → `production`.
- Workflow chép `compose.yaml`, `Caddyfile`, `postgres/`, `scripts/` lên server, đặt `IMAGE_TAG` trong `.env`, rồi `docker compose pull && docker compose up -d`. Migration chạy tự động trước khi API khởi động.
- **Quay lại bản trước**: trên server sửa `IMAGE_TAG` trong `.env` về SHA cũ rồi chạy `docker compose up -d`. Migration không tự đảo ngược, nên mọi migration phải tương thích ngược với bản code trước đó (thêm cột/bảng trước, xoá ở bản sau).

`docker compose up -d` tạo lại mọi bản sao cùng lúc, nên mỗi lần deploy có vài giây gián đoạn. Đủ cho pilot; khi cần không gián đoạn thì chuyển sang deploy cuốn chiếu (rolling).

## Tạo trung tâm (tenant)

API không mở ra Internet, nên gọi từ trong mạng nội bộ của stack:

```bash
cd /opt/konnyaku && set -a && . ./.env && set +a
docker run --rm --network konnyaku_app curlimages/curl -s -X POST http://api:8080/api/platform/tenants \
  -H "X-Platform-Key: $PLATFORM_ADMIN_KEY" -H "Content-Type: application/json" \
  -d '{"slug":"abc","name":"Trung tâm ABC","defaultLocale":"vi","enabledLocales":["vi","en"],"timeZone":"Asia/Ho_Chi_Minh"}'
```

Sau đó tạo quản trị viên đầu tiên bằng `POST /api/identity/bootstrap/tenant-admin` theo cùng cách (xem `infra/scripts/seed_demo.py`). Lần đầu mở `https://abc.BASE_DOMAIN`, Caddy hỏi web app host này có phải tenant không rồi mới xin chứng chỉ, nên lần tải đầu chậm vài giây.

Bảo mật tài khoản ở production (mặc định, không cần đặt biến):

- Quản trị và Kế toán **bắt buộc xác thực 2 bước**: lần đăng nhập đầu tiên, quản trị viên vừa tạo được hướng dẫn quét mã QR và lưu mã khôi phục. Tắt bắt buộc (không khuyến khích): `Auth__TwoFactor__Enforce=false`; đổi danh sách vai trò: `Auth__TwoFactor__RequiredRoles__0=…`.
- Khoá TOTP lưu mã hoá AES-GCM bằng khoá suy ra từ `Auth__SecretKey` (nếu không đặt thì từ `Auth__SigningKey`). Đổi khoá này làm mọi 2FA đã bật không dùng được nữa.
- Link quên mật khẩu dùng `https://<host của trung tâm>/login/reset`; đặt `Site__PublicScheme` / `Site__PublicPort` nếu web không chạy https cổng 443. Email chưa được gửi cho tới khi có module Notification (log API ghi cảnh báo, không ghi link).
- Endpoint mô phỏng buổi học (`/api/workspace/dev/...`) tắt; chỉ bật cho staging bằng `Workspace__DevTools=true`.

## Backup

```bash
crontab -e
```

Thêm dòng:

```
15 2 * * * cd /opt/konnyaku && ./scripts/backup.sh >> backups/backup.log 2>&1
```

`backup.sh` chạy `pg_dump` toàn bộ database (mọi schema module, kể cả sổ buổi học), kiểm tra file đọc được, giữ `BACKUP_KEEP_DAYS` ngày trên máy. **Bản trên cùng máy không phải backup**: cài `rclone`, cấu hình một remote object storage ở nhà cung cấp khác và đặt `RCLONE_REMOTE` trong `.env` để mỗi bản được đẩy đi.

### Khôi phục

Diễn tập mỗi tháng trên staging:

```bash
docker compose stop web api worker
docker compose exec -T postgres pg_restore -U konnyaku -d konnyaku --clean --if-exists /backups/<file>.dump
docker compose up -d
```

`pg_dump` cho điểm khôi phục theo ngày. Khi có doanh thu, chuyển sang managed PostgreSQL hoặc pgBackRest để khôi phục tới từng thời điểm.

## Vận hành hằng ngày

| Việc | Lệnh (trong `/opt/konnyaku`) |
| --- | --- |
| Trạng thái | `docker compose ps` |
| Log một thành phần | `docker compose logs -f --tail 200 api` |
| Tăng bản sao web | `WEB_REPLICAS=3` trong `.env`, rồi `docker compose up -d` |
| Vào psql | `docker compose exec postgres psql -U konnyaku` |
| Dung lượng | `docker system df` |
| Phòng học đang mở | `docker compose exec caddy wget -qO- http://signaling:5100/rtc/health` (số phòng, số người) |
| Kiểm tra TURN | Mở <https://webrtc.github.io/samples/src/content/peerconnection/trickle-ice/>, nhập `turn:BASE_DOMAIN:3478` với username/credential lấy từ một vé phòng học; phải thấy candidate `relay` |
