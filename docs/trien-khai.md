# Triển khai Konnyaku lên VPS

Pilot chạy trên **một VPS** bằng Docker Compose. Toàn bộ định nghĩa nằm trong `infra/deploy/`. Image do GitHub Actions build và đẩy lên GHCR (`.github/workflows/deploy.yml`), cho cả **amd64** và **arm64**: cùng một tag, Docker trên server tự kéo bản đúng với CPU.

## Kiến trúc

```
Internet ─80/443─► Caddy ──► web ×2 (Next.js: trang công khai, /learn, /teach, /ops; /platform trên admin.BASE_DOMAIN)
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
| `caddy` | 1 | Cổng duy nhất mở ra Internet. HTTPS tự động (on-demand) cho `{slug}.BASE_DOMAIN`, tên miền riêng và `admin.BASE_DOMAIN`; chia tải round-robin giữa các bản sao `web`, bỏ qua bản sao đang khởi động lại |
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

## Bảng điều khiển nền tảng và tạo trung tâm

Người vận hành Konnyaku quản lý các trung tâm tại **`https://admin.BASE_DOMAIN/platform`** (host quản trị; DNS `*.BASE_DOMAIN` đã trỏ về server, Caddy tự cấp chứng chỉ vì `tls-ask` nhận host này). Khu vực `/platform` chỉ phục vụ trên host quản trị: trên host trung tâm nó trả 404, và API từ chối mọi lệnh của bảng điều khiển gửi từ host khác. `admin` là mã được nền tảng giữ lại, không trung tâm nào dùng được.

Tài khoản nền tảng tách hẳn khỏi tài khoản trung tâm: bảng `platform.operator`, token riêng (audience `platform`, không mang `tid`), cookie riêng `kk_platform`. Đăng nhập bằng email + mật khẩu (tối thiểu 12 ký tự) + **mã TOTP bắt buộc** từ ứng dụng xác thực; 10 mã khôi phục dùng một lần được cấp khi bật TOTP; sai 5 lần (mật khẩu hoặc mã) thì khóa 15 phút. Secret TOTP được mã hoá trong DB bằng khóa suy ra từ `Platform__SecretKey` (nếu không đặt thì từ `AUTH_SIGNING_KEY`): đổi khóa này thì mọi người vận hành phải bật lại TOTP.

### Tạo người vận hành đầu tiên (một lần)

Endpoint bootstrap chỉ chạy được khi **chưa có người vận hành nào**, và cần `X-Platform-Key`. API không mở ra Internet, nên gọi từ trong mạng nội bộ của stack:

```bash
cd /opt/konnyaku && set -a && . ./.env && set +a
docker run --rm --network konnyaku_app curlimages/curl -s -X POST http://api:8080/api/platform/operators/bootstrap   -H "X-Platform-Key: $PLATFORM_ADMIN_KEY" -H "Content-Type: application/json"   -d '{"email":"ten.ban@congty.vn","displayName":"Tên bạn","password":"<mật khẩu dài, ít nhất 12 ký tự>"}'
```

Rồi mở `https://admin.BASE_DOMAIN/platform`, đăng nhập bằng email + mật khẩu đó: lần đầu trang hiện mã QR để thêm vào ứng dụng xác thực, nhập mã 6 số để xác nhận, rồi **lưu 10 mã khôi phục** (chỉ hiện một lần). Sau đó mời những người vận hành khác ở mục **Người vận hành** (mật khẩu tạm hiện một lần; họ bật TOTP và đặt mật khẩu mới ở lần đăng nhập đầu). Không gửi mật khẩu trong lệnh lên lịch sử shell dùng chung: chạy lệnh trên trong một phiên riêng hoặc xoá dòng khỏi `~/.bash_history`.

### Tạo trung tâm

Trong bảng điều khiển: **Trung tâm → Tạo trung tâm**, nhập tên, mã (tên miền con), ngôn ngữ, múi giờ và quản trị viên đầu tiên. Màn hình hiện địa chỉ `https://<mã>.BASE_DOMAIN` và mật khẩu tạm của quản trị viên (một lần). Lần đầu mở địa chỉ đó, Caddy hỏi web app host này có thuộc trung tâm nào không rồi mới xin chứng chỉ, nên lần tải đầu chậm vài giây.

Cũng trong trang của từng trung tâm: **tạm khóa / mở lại** (khi khóa, mọi đăng nhập và lệnh API của trung tâm bị từ chối với mã `platform.tenant_suspended`, trang công khai hiện thông báo tạm ngừng; mỗi bản sao API thấy thay đổi trong tối đa 1 phút vì cache tenant), **thêm tên miền riêng** (khách trỏ DNS về server trước; không nhận tên miền thuộc `BASE_DOMAIN`), **thêm quản trị viên** (khi quản trị viên cũ nghỉ). Mọi thao tác — kể cả đăng nhập, khóa tài khoản do sai nhiều lần — nằm ở **Nhật ký** (bảng `platform.audit_entry`, role `kk_platform` chỉ được thêm và đọc).

### `X-Platform-Key` chỉ cho tự động hoá

`PLATFORM_ADMIN_KEY` vẫn mở `POST /api/platform/tenants`, `POST /api/identity/bootstrap/tenant-admin` và bootstrap người vận hành, cho script, seed và CI. Khóa này không gắn với người nào (nhật ký ghi "tự động hoá"), nên không dùng cho việc vận hành hằng ngày; giữ trong `.env`, đổi định kỳ, không đưa cho người vận hành.

### Đổi host quản trị

Mặc định `admin.BASE_DOMAIN`. Muốn dùng host khác: đặt cùng một giá trị cho `Platform__AdminHost` (api, worker) và `KONNYAKU_ADMIN_HOST` (web) trong `compose.yaml`, và trỏ DNS host đó về server. Muốn hạn chế thêm theo IP (VPN văn phòng), thêm khối `@admin host admin.<domain>` + `remote_ip` trong `Caddyfile`.

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
