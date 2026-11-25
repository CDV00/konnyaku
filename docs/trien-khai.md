# Triển khai Konnyaku lên VPS

Pilot chạy trên **một VPS** bằng Docker Compose. Toàn bộ định nghĩa nằm trong `infra/deploy/`. Image do GitHub Actions build và đẩy lên GHCR (`.github/workflows/deploy.yml`), cho cả **amd64** và **arm64**: cùng một tag, Docker trên server tự kéo bản đúng với CPU.

## Kiến trúc

```
Internet ─80/443─► Caddy ──► web ×2 (Next.js: trang công khai, /learn, /teach, /ops)
                    │           │  (mạng app)
                    │           ▼
                    │        api ×2 (ASP.NET Core) ──┐
                    │                                ├─► PostgreSQL 17 (mạng data, internal)
                    │        worker ×1 ──────────────┘
                    │        migrator (chạy mỗi lần up, xong thì thoát)
                    └─ chứng chỉ HTTPS cấp tự động cho từng host tenant
```

| Thành phần | Bản sao | Ghi chú |
| --- | --- | --- |
| `caddy` | 1 | Cổng duy nhất mở ra Internet. HTTPS tự động (on-demand) cho `{slug}.BASE_DOMAIN`; chia tải round-robin giữa các bản sao `web`, bỏ qua bản sao đang khởi động lại |
| `web` | `WEB_REPLICAS` (2) | Không lưu trạng thái. Gọi API qua mạng nội bộ, chuyển IP khách trong `X-Forwarded-For` |
| `api` | `API_REPLICAS` (2) | Không mở ra Internet. Chỉ tin `X-Forwarded-For` từ mạng `app` (`APP_SUBNET`). Cache tenant trong bộ nhớ 1 phút |
| `worker` | `WORKER_REPLICAS` (1) | Chạy nhiều bản an toàn: outbox lấy việc bằng `FOR UPDATE SKIP LOCKED` |
| `migrator` | — | Áp migration bằng role chủ schema `konnyaku`; `api`/`worker` chỉ khởi động khi migrator thành công |
| `postgres` | 1 | Mạng `data` không có đường ra Internet. Mỗi module một role `kk_<module>` với mật khẩu riêng |

Giới hạn khi chạy nhiều bản sao: giới hạn tần suất form đăng ký công khai được đếm **riêng từng bản sao API** (2 bản sao ≈ gấp đôi hạn mức). Khi cần chính xác, chuyển bộ đếm sang Redis.

## Chuẩn bị (một lần)

1. **VPS**: Ubuntu 24.04, 4 vCPU / 8 GB RAM / 100 GB SSD, x86 hoặc ARM (ví dụ Oracle Cloud Always Free, Ampere A1). Máy test 2 GB RAM chạy được nếu đặt `WEB_REPLICAS=1` và `API_REPLICAS=1`. Cài Docker Engine và plugin compose theo hướng dẫn chính thức của Docker. Mở firewall cổng 22, 80, 443 (TCP) và 443 (UDP).
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

   Sửa trong `.env`: `BASE_DOMAIN`, `ACME_EMAIL`, `IMAGE_*` (thay `OWNER` bằng tên GitHub org/user viết **chữ thường**, ví dụ `ghcr.io/cdv00/konnyaku-web`). Lưu một bản `.env` vào trình quản lý mật khẩu của nhóm: mất file này là mất mật khẩu DB và khóa ký JWT.

5. **GitHub**: Settings → Environments, tạo `staging` và `production`. Mỗi environment có secrets `DEPLOY_HOST`, `DEPLOY_USER`, `DEPLOY_SSH_KEY` (khóa riêng của một user chỉ dùng để deploy, thuộc nhóm `docker`), `DEPLOY_KNOWN_HOSTS` (kết quả `ssh-keyscan -H <host>`), và biến `DEPLOY_PATH` nếu khác `/opt/konnyaku`. Nên bật *Required reviewers* cho `production`.

## Deploy

- Mỗi PR: CI build 4 image trên máy amd64 và máy arm64 thật của GitHub rồi chạy thử (`infra/deploy/scripts/smoke-test.sh`), không đẩy lên registry.
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
| Vào psql | `docker compose exec postgres psql -U konnyaku` |
| Dung lượng | `docker system df` |
