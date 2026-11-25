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

   Sửa trong `.env`: `BASE_DOMAIN`, `ACME_EMAIL`, `IMAGE_*` (thay `OWNER` bằng tên GitHub org/user viết **chữ thường**, ví dụ `ghcr.io/cdv00/konnyaku-web`). Server đã chạy từ trước bản có phòng học: thêm `SIGNALING_SECRET` và `TURN_SECRET` (chạy lại `gen-secrets.sh` rồi chỉ chép hai dòng đó) và `IMAGE_SIGNALING`. Lưu một bản `.env` vào trình quản lý mật khẩu của nhóm: mất file này là mất mật khẩu DB và khóa ký JWT. `gen-secrets.sh` cũng sinh `CAPTCHA_KEY` (xem mục [Captcha của form công khai](#captcha-của-form-công-khai)); `.env` có sẵn từ trước thì thêm tay một dòng `CAPTCHA_KEY=$(openssl rand -hex 32)`, nếu không `docker compose up` báo thiếu biến.

5. **GitHub**: Settings → Environments, tạo `staging` và `production`. Mỗi environment có secrets `DEPLOY_HOST`, `DEPLOY_USER`, `DEPLOY_SSH_KEY` (khóa riêng của một user chỉ dùng để deploy, thuộc nhóm `docker`), `DEPLOY_KNOWN_HOSTS` (kết quả `ssh-keyscan -H <host>`), và biến `DEPLOY_PATH` nếu khác `/opt/konnyaku`. Nên bật *Required reviewers* cho `production`.

## Deploy

- Mỗi PR: CI build 5 image (api, worker, migrator, web, signaling) trên máy amd64 và máy arm64 thật của GitHub rồi chạy thử (`infra/deploy/scripts/smoke-test.sh`), không đẩy lên registry.
- Merge vào `main` → CI xanh → workflow **Deploy** build lại trên từng kiến trúc, chạy thử, đẩy `<sha>-amd64` / `<sha>-arm64`, gộp thành tag đa kiến trúc `<sha>` và `latest`, rồi deploy lên **staging**.
- Lên **production**: Actions → Deploy → Run workflow → `production`.
- Workflow chép `compose.yaml`, `Caddyfile`, `postgres/`, `scripts/` lên server, đặt `IMAGE_TAG` trong `.env`, rồi `docker compose pull && docker compose up -d`. Migration chạy tự động trước khi API khởi động.
- **Quay lại bản trước**: trên server sửa `IMAGE_TAG` trong `.env` về SHA cũ rồi chạy `docker compose up -d`. Migration không tự đảo ngược, nên mọi migration phải tương thích ngược với bản code trước đó (thêm cột/bảng trước, xoá ở bản sau).

`docker compose up -d` tạo lại mọi bản sao cùng lúc, nên mỗi lần deploy có vài giây gián đoạn. Đủ cho pilot; khi cần không gián đoạn thì chuyển sang deploy cuốn chiếu (rolling).

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
| Phòng học đang mở | `docker compose exec caddy wget -qO- http://signaling:5100/rtc/health` (số phòng, số người) |
| Kiểm tra TURN | Mở <https://webrtc.github.io/samples/src/content/peerconnection/trickle-ice/>, nhập `turn:BASE_DOMAIN:3478` với username/credential lấy từ một vé phòng học; phải thấy candidate `relay` |
