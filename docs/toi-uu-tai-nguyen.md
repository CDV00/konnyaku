# Tối ưu tài nguyên (RAM, CPU) của stack production

Tài liệu này trả lời ba câu hỏi: RAM của stack `infra/deploy` đi đâu, chỉnh gì để chạy gọn trên máy 2 GB và 8 GB, và có nên viết lại service nào bằng Rust không. Mọi con số đều đo trên stack production thật (image build từ repo, Caddy → web → api → PostgreSQL), không phải ước lượng.

## Tóm tắt

- **RAM đi đâu** (cấu hình cũ, 2 web + 2 api, có tải): web Next.js ~50 %, PostgreSQL ~18 %, API .NET ~22 %, worker ~7 %, Caddy ~2 %.
- **Ba nguyên nhân chính, đều sửa bằng cấu hình**:
  1. Node không có trần heap: mỗi bản sao web lên 300–620 MiB khi có tải và không trả lại sau tải.
  2. 11 connection pool (mỗi module một role DB) không giới hạn: ở 1 api + 1 worker đã mở **93/100** kết nối PostgreSQL, mỗi kết nối vài MB; các kết nối rảnh giữ 5 phút.
  3. Container worker riêng tốn ~85 MiB dù gần như không làm gì trên máy nhỏ.
- **Kết quả**: RAM đỉnh (tổng đỉnh từng container) giảm từ **~1 250 → ~880 MiB** (−30 %) ở hồ sơ 8 GB và **~860 → ~470 MiB** (−45 %) ở hồ sơ 2 GB khi có tải thường; sau tải, stack nhả RAM về **~620 / ~335 MiB** thay vì giữ ~0,93–1,17 GB / ~0,83 GB. Khi stress: 8 GB ~1 560 → 1 080–1 375 MiB, 2 GB ~1 100 → 630–780 MiB.
- **Sửa thêm một lỗi có sẵn**: Caddy thỉnh thoảng trả 502 cho POST (đăng nhập, gửi form) do Node đóng kết nối keep-alive sau 5 s; với một bản sao web, một lỗi như vậy làm Caddy coi web là hỏng và trả 503 trong 30 s (đo được 7 % lỗi ở một lượt). Sau khi đặt `KEEP_ALIVE_TIMEOUT`: 0 lỗi ở cả 4 lượt đo cuối.
- **GC .NET, ReadyToRun**: đã đo, không đáng kể (API ~120–150 MiB chủ yếu là runtime, mã JIT và model EF, không phải heap) nên không đưa vào cấu hình.
- **CPU mới là giới hạn**: một trang SSR tốn ~100 ms CPU, mỗi bản sao web (một luồng JavaScript) đạt cỡ 10 trang/giây; web chiếm hơn một nửa CPU của stack. Kịch bản *load* (22 người dùng ảo liên tục) đã làm một bản sao web bão hòa; muốn phục vụ nhiều hơn cần thêm vCPU (và bản sao web), không phải thêm RAM.
- **Bộ giám sát** (profile `monitoring`): thêm ~250 MiB lúc rảnh, ~600 MiB khi có tải; chạy chung được trên máy 8 GB, máy 2 GB nên đặt trên VPS riêng ([chi tiết](#bộ-giám-sát)).
- **Rust**: **không tách** service nào lúc này. Phần tốn RAM nhất (web, SSR React) không viết lại bằng Rust được; viết lại API (~7 400 dòng C#, 11 module) tiết kiệm ~100 MiB mỗi bản sao, ít hơn những gì cấu hình đã lấy lại, với chi phí hàng tuần và rủi ro cao. Chi tiết và điều kiện xem xét lại ở [cuối tài liệu](#rust-có-nên-tách-service-không).

## Cách đo

| Mục | Giá trị |
| --- | --- |
| Máy | Laptop Windows 11, Docker Desktop (WSL2) 8 vCPU / 11,6 GB cho VM, amd64. Máy **dùng chung** với các phiên làm việc khác (build Rust, test tích hợp), nên **thông lượng và độ trễ dao động mạnh giữa các lần chạy** (cùng cấu hình: 5,4 → 13 req/s). RAM ổn định giữa các lần (±10 MiB) |
| Phiên bản | `main` tại `0caaa04`: 11 module, chưa có module Notification, service `signaling` (Rust) và `coturn` của phòng học. Hai service này đo riêng ở [mục 9](#9-phòng-học-signaling-rust-và-coturn) |
| Stack | `infra/deploy/compose.yaml` với Caddyfile HTTP (`auto_https off`), cổng 8089, tenant `demo` từ `infra/scripts/seed_demo.py` |
| Đo RAM/CPU | `docker stats` mỗi ~2 s: lúc rảnh (45 s sau khi khởi động), trong lúc tải (đỉnh, CPU trung bình) và 30 s sau tải ("sau tải": RAM mà tiến trình còn giữ) |
| Tải | k6 trong container, gọi qua Caddy ([`infra/loadtest/mixed.js`](../infra/loadtest/mixed.js)), 2 phút: 10 khách xem trang chủ → form đăng ký → gửi lead; 5 nhân viên đăng nhập rồi mở `/ops`, `/ops/classes`, `/ops/students`, `/ops/leads`; 5 học viên mở `/learn`; 2 người đăng nhập liên tục (PBKDF2). **load**: nghỉ 1 s giữa các vòng (22 người dùng ảo). **stress**: gấp 4 số người, không nghỉ |
| Giới hạn form công khai | Nâng `RateLimits__PublicFormsPerMinute` khi đo (mọi request đến từ một IP) |

Tái hiện trên một stack đang chạy (tên project `<p>`, mạng `<p>_edge`):

```bash
infra/loadtest/measure.sh <p> base-2x2 load <mật khẩu tạm học viên lần đầu>
infra/loadtest/measure.sh <p> base-2x2 stress
```

`measure.sh` ghi `<nhãn>.summary.txt` (RAM rảnh/đỉnh/sau tải và CPU từng container), `<nhãn>.stats.csv` (mọi mẫu) và `<nhãn>.k6.txt` (p95 theo trang). Học viên mẫu phải đổi mật khẩu tạm một lần; script làm việc đó khi nhận mật khẩu tạm.

## Trước khi tối ưu

Cấu hình cũ: không giới hạn bộ nhớ, Node mặc định, Npgsql mặc định (pool tối đa 100, giữ kết nối rảnh 300 s), PostgreSQL mặc định (`max_connections=100`, `shared_buffers=128MB`).

Mỗi ô: **rảnh / đỉnh / sau tải** (MiB), cộng các bản sao (×2). *Tổng* cộng đỉnh của từng container (không nhất thiết cùng lúc), nên là cận trên. Lượt *stress* chạy ngay sau lượt *load* trên cùng container, nên cột "rảnh" của nó là RAM còn giữ sau lượt load. CPU stack: tổng % lõi trung bình. req/s, p95 và lỗi phụ thuộc mạnh vào tải của máy dùng chung (xem [Cách đo](#cách-đo)); hai lần đo cùng cấu hình cho thấy biên độ đó.

| Cấu hình | web | api | worker | postgres | caddy | **Tổng** | CPU stack | req/s | p95 | lỗi |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 2 web + 2 api, load (lần 1) | 82 / 666 / 571 ×2 | 83 / 275 / 268 ×2 | 77 / 83 / 82 | 53 / 235 / 228 | 12 / 21 / 19 | **307 / 1280 / 1168** | 330 % | 21.7 | 1.83s | 0.00% |
| 2 web + 2 api, load (lần 2) | 94 / 648 / 333 ×2 | 81 / 261 / 256 ×2 | 75 / 79 / 80 | 51 / 238 / 238 | 12 / 22 / 21 | **313 / 1248 / 928** | 385 % | 16.9 | 2.96s | 0.04% |
| 2 web + 2 api, stress (lần 1) | 306 / 914 / 664 ×2 | 268 / 290 / 286 ×2 | 83 / 86 / 84 | 228 / 231 / 105 | 19 / 34 / 32 | **904 / 1555 / 1171** | 367 % | 9.1 | 24.87s | 0.51% |
| 2 web + 2 api, stress (lần 2) | 272 / 940 / 750 ×2 | 257 / 272 / 270 ×2 | 79 / 84 / 82 | 238 / 239 / 112 | 21 / 36 / 34 | **867 / 1571 / 1248** | 350 % | 10.3 | 25.79s | 0.13% |
| 1 web + 1 api, load (lần 1) | 41 / 405 / 357 | 41 / 140 / 138 | 73 / 86 / 85 | 51 / 216 / 216 | 12 / 21 / 20 | **218 / 868 / 816** | 241 % | 9.8 | 5.28s | 0.00% |
| 1 web + 1 api, load (lần 2) | 54 / 369 / 369 | 40 / 148 / 140 | 79 / 91 / 91 | 51 / 229 / 229 | 14 / 22 / 19 | **238 / 859 / 848** | 226 % | 4.8 | 10.76s | 7.15% |
| 1 web + 1 api, stress (lần 1) | 225 / 620 / 525 | 137 / 142 / 140 | 86 / 91 / 88 | 216 / 217 / 108 | 20 / 35 / 31 | **684 / 1105 / 892** | 241 % | 11.5 | 15.77s | 0.00% |
| 1 web + 1 api, stress (lần 2) | 160 / 578 / 517 | 140 / 157 / 156 | 92 / 102 / 103 | 229 / 229 / 117 | 19 / 35 / 34 | **640 / 1101 / 927** | 255 % | 7.3 | 20.35s | 0.00% |

Quan sát:

- **web**: khởi động ~40 MiB, lên 300–620 MiB khi có tải và **giữ nguyên sau tải**: V8 chỉ dọn mạnh khi gần trần heap mặc định (~2 GB trên máy này).
- **PostgreSQL**: 50 → ~230 MiB, gần như toàn bộ do số kết nối. Ở 1×1 khi có tải: `kk_platform` 23, `kk_identity` 13, `kk_crm` 11, `kk_academic` 11, … tổng **93/100**. Ở 2×2 stress có 7 request lỗi. Nguyên nhân: mỗi module một role nên mỗi process có 11 pool; khi cache tenant (1 phút) hết hạn, hàng chục request cùng truy vấn và pool phình ra, rồi giữ kết nối rảnh 5 phút.
- **api**: ~40 MiB lúc rảnh, ~140 MiB sau request đầu tiên của mỗi module (JIT, model EF Core 11 DbContext) và ổn định ở đó.
- **worker**: ~80 MiB, CPU ~5 %.
- **Caddy**: 20–35 MiB.

## Từng thay đổi và hiệu quả

Đo riêng từng nhóm để biết cái nào thật sự có ích.

### 1. Giới hạn connection pool (có hiệu quả)

Thêm cấu hình `Database:MaxPoolSize` và `Database:ConnectionIdleLifetime` (áp lên connection string của mọi module, `ServiceCollectionExtensions.WithPoolSettings`). Compose đặt `DB_POOL_MAX=6` cho api, `WORKER_DB_POOL_MAX=3` cho worker, đóng kết nối rảnh sau 60 s. Quy tắc: `(API_REPLICAS × DB_POOL_MAX + WORKER_REPLICAS × WORKER_DB_POOL_MAX) × số module < PG_MAX_CONNECTIONS`. Với 12 module hiện nay (đã thêm Notification): 8 GB (2×6 + 1×3)×12 = 180 < 200; 2 GB 6×12 = 72 < 80. Thêm module thì kiểm tra lại công thức này.

PostgreSQL ở hồ sơ 2 GB (1 api): khi có tải **~220 → ~110 MiB**, sau tải **~225 → ~50–60 MiB**. Ở hồ sơ 8 GB (2 api + worker, tối đa 165 kết nối khi đo với 11 module) đỉnh khi có tải vẫn ~200–225 MiB, nhưng sau tải **~235 → ~70–100 MiB** vì kết nối rảnh được đóng sau 60 s. Tổng số kết nối không còn vượt được `max_connections` khi thêm bản sao. Không có lỗi pool (`53300`, `connection pool has been exhausted`) trong mọi lượt đo.

### 2. Trần heap Node (có hiệu quả, chọn mức vừa)

`NODE_OPTIONS=--max-old-space-size=${WEB_HEAP_MB}`. Đo riêng web ở tốc độ cố định 4 trang/giây (không bão hòa, 2 lượt xen kẽ):

| Trần heap | RAM web khi tải (MiB) | CPU mỗi trang (ms) | p95 |
| --- | --- | --- | --- |
| không trần (2048) | 159–241 | 122–196 | 159–199 ms |
| 512 | 111–118 | 134–161 | 172–178 ms |
| 384 | 109–141 | 141–147 | 178–182 ms |
| 256 | 106–117 | 102–149 | 128–213 ms |
| 160 (bản thử đầu) | — | — | **hết heap và khởi động lại khi stress** (`JavaScript heap out of memory`) |

Chênh CPU giữa các mức nằm trong độ nhiễu của máy; RAM giảm rõ. Dưới ~256 MB, Next.js không đủ heap khi nhiều trang render cùng lúc. Chọn **512** (8 GB) và **384** (2 GB). Tăng `--max-semi-space-size` lên 32 MB làm RAM tăng (~280 MiB) mà không thấy lợi về CPU nên không dùng.

### 3. Chạy outbox trong API trên máy nhỏ (có hiệu quả)

`Worker__InProcess=true` (`API_RUNS_WORKER`) đăng ký các vòng outbox của mọi module vào process API, rồi đặt `WORKER_REPLICAS=0`. An toàn khi nhiều process cùng xử lý vì outbox lấy việc bằng `FOR UPDATE SKIP LOCKED`. API lúc rảnh tăng ~40 → ~90 MiB (khởi tạo model EF của mọi module ngay), khi có tải vẫn ~145 MiB; bỏ được container worker ~85 MiB. Lợi ích ròng **~80 MiB**. Máy 8 GB vẫn giữ worker riêng (cô lập lỗi, scale riêng).

### 4. Tham số PostgreSQL theo cỡ máy

| Tham số | 2 GB | 8 GB | Lý do |
| --- | --- | --- | --- |
| `max_connections` | 80 | 200 | Trên tổng pool (mục 1) |
| `shared_buffers` | 128MB | 512MB | ~6–8 % RAM; phần còn lại để page cache của hệ điều hành. RAM container chỉ tăng khi dữ liệu thật được đọc |
| `effective_cache_size` | 512MB | 3GB | Chỉ là gợi ý cho planner |
| `work_mem` | 4MB | 8MB | Mỗi thao tác sort/hash; nhân với số kết nối |
| `maintenance_work_mem` | 64MB | 128MB | VACUUM, tạo index |

Với dữ liệu demo nhỏ, `shared_buffers` lớn hơn chưa làm RAM tăng; con số sẽ tăng dần theo kích thước dữ liệu tới trần đã đặt.

### 5. Đã thử, không giữ

| Thử | Kết quả | Quyết định |
| --- | --- | --- |
| `DOTNET_GCConserveMemory=5` | API đỉnh 119–121 MiB, như mặc định | Không dùng |
| `DOTNET_GCgen0size=4MB` | Như mặc định | Không dùng |
| ReadyToRun (`PublishReadyToRun`) | Rảnh −10 MiB, khi tải như cũ; image +24 MB, build lâu hơn | Không dùng |
| Trần heap Node 160–256 MB | Hết heap khi stress / p95 xấu hơn | Dùng 384–512 |

API .NET đã chạy Workstation GC (`DOTNET_gcServer=0` trong Dockerfile); heap được quản lý chỉ vài chục MB, nên chỉnh GC không còn gì để lấy. Giới hạn bộ nhớ container (`API_MEM_LIMIT`) làm .NET tự đặt trần heap 75 % giới hạn, đủ làm lưới an toàn.

### 6. Giới hạn bộ nhớ container

Mỗi service có `deploy.resources.limits.memory` (`WEB_MEM_LIMIT`, `API_MEM_LIMIT`, `WORKER_MEM_LIMIT`, `PG_MEM_LIMIT`). Đây là **trần chống rò rỉ**, không phải phần giữ trước: một container vượt trần bị khởi động lại riêng nó (Caddy chuyển request sang bản sao khác), thay vì cả máy bị OOM killer của nhân Linux giết ngẫu nhiên (thường là PostgreSQL).

### 7. Keep-alive giữa Caddy và web (sửa lỗi có sẵn)

Trong mọi lượt đo (cả cấu hình cũ) có vài request POST bị Caddy trả **502**: Next.js (Node) đóng kết nối rảnh sau 5 s, còn Caddy giữ kết nối tới upstream tới 2 phút và đôi khi gửi request lên đúng kết nối đang đóng; GET thì Caddy thử lại được, POST thì không. Nặng hơn: Caddyfile đặt `fail_duration 30s`, nên khi chỉ có **một** bản sao web, một lỗi như vậy khiến Caddy coi web hỏng và trả **503 cho mọi request trong 30 s** (lượt đo cấu hình cũ 1×1: 7 % lỗi, log Caddy `no upstreams available`).

Sửa: `KEEP_ALIVE_TIMEOUT=130000` cho web (server standalone của Next.js đọc biến này), dài hơn thời gian Caddy giữ kết nối. Bốn lượt đo cuối (2 hồ sơ × load/stress): 0 lỗi trên 5 660 request.

### 8. Log của Worker

Worker ghi log mỗi lần quét outbox (11 module × mỗi 2 s ≈ 5 câu SQL mỗi giây khi rảnh, cấp Information). Hạ `Microsoft.EntityFrameworkCore.Database.Command` xuống Warning trong `appsettings.json` của Worker (API đã làm vậy qua Serilog). Không đổi RAM, bớt CPU và dung lượng log.

### 9. Phòng học: signaling (Rust) và coturn

Hai service thêm vào sau đợt đo chính, nên đo riêng: container signaling đứng một mình, k6 mở WebSocket với vé ký bằng `SIGNALING_SECRET` ([`infra/loadtest/signaling.js`](../infra/loadtest/signaling.js)), mỗi phòng 6 người, mỗi người đổi trạng thái mic và gửi một tin `signal` ~1 KB tới từng người khác mỗi 2 giây, giữ 60 s.

| Tải | RAM đỉnh | CPU trung bình | Sau khi mọi người rời phòng |
| --- | --- | --- | --- |
| Rảnh | ~1 MiB | 0 % | — |
| 50 phòng, 300 người (~870 tin/giây vào) | 66 MiB | 12 % | 16 MiB |
| 200 phòng, 1 200 người (~3 400 tin/giây vào) | 250–255 MiB | 44–66 % | 35–45 MiB |

- Khoảng **210 KiB mỗi người đang kết nối**, tăng tuyến tính, không rò rỉ (trả lại sau khi rời phòng). Phần lớn có lẽ là bộ đệm đọc/ghi mặc định 128 KiB của tungstenite cho mỗi WebSocket; tin signaling chỉ vài KB nên có thể hạ xuống ~16 KiB nếu cần (chưa đo).
- `MALLOC_ARENA_MAX=2` không đổi gì (đã thử), nên không dùng.
- coturn lúc rảnh ~7 MiB; chưa đo khi relay thật (cần client TURN). Dải cổng relay mặc định 101 cổng giới hạn số kết nối relay đồng thời.

Trần: `SIGNALING_MEM_LIMIT` 256m ở hồ sơ 8 GB (~1 100 người cùng lúc), 96m ở hồ sơ 2 GB (~400 người, tức ~65 lớp 6 người — quá đủ cho máy 1–2 vCPU); `COTURN_MEM_LIMIT` 128m / 64m. Phòng nằm trong bộ nhớ của một bản signaling duy nhất: chạm trần thì mọi phòng rớt, nên trần đặt theo giờ cao điểm chứ không sát số đo. Không có GC hay heap để chỉnh như .NET/Node.

### 10. Socket realtime `/rt` (chuông thông báo)

Cùng service `signaling` (doc 10.9): mỗi tab đã đăng nhập giữ một WebSocket `/rt`, service `PSUBSCRIBE user:*` trên Redis. Đo container đứng một mình với Redis, k6 mở tab bằng vé ký với `SIGNALING_SECRET` ([`infra/loadtest/realtime.js`](../infra/loadtest/realtime.js), 2 tab mỗi người), trong lúc đó `redis-cli` phát 2 000 tín hiệu:

| Tải | RAM | CPU | Ghi chú |
| --- | --- | --- | --- |
| Rảnh (đã subscribe Redis) | ~1,4 MiB | 0 % | |
| 2 000 tab của 1 000 người | ~32 MiB | ~0 % | 4 000/4 000 tín hiệu tới đúng tab |
| 30 s sau khi đóng hết | ~24 MiB | 0 % | Bộ cấp phát giữ lại, không tăng thêm ở lượt sau |

- Khoảng **15 KiB mỗi tab**: bộ đệm đọc/ghi của socket `/rt` đặt 4 KiB (tín hiệu chỉ vài chục byte), thay vì 128 KiB mặc định như phòng học.
- Trần `SIGNALING_MEM_LIMIT` giữ nguyên (256m / 96m): 1 000 người trong phòng học (~210 MiB) **hoặc** ~15 000 tab; máy 2 GB với 96m đủ ~400 người trong phòng hoặc ~5 000 tab. Redis không lưu gì cho pub/sub, `REDIS_MAXMEMORY` không đổi.
- Mỗi tab tự kết nối lại sau `RT_CONNECTION_MINUTES` (60) để lấy vé mới; mỗi người tối đa `RT_MAX_CONNECTIONS_PER_USER` (20) socket trên mỗi bản.

### 11. Kho file: Garage, files-backup, ClamAV

File không đi qua API (URL ký, trình duyệt gửi thẳng lên Garage qua Caddy), nên api và web không tăng RAM theo dung lượng file. Phần API vẫn làm khi hoàn tất một upload: đọc 512 byte đầu (magic bytes), băm SHA-256 theo luồng (bộ đệm 80 KB), và riêng ảnh thì đọc cả ảnh vào RAM để xoá EXIF (ảnh tối đa 20 MB, một ảnh mỗi request).

Garage v2.5.0 (một node, metadata SQLite, `block_ram_buffer_max = 32MiB`) đo bằng `docker stats` khi chạy một mình: **~8 MiB** lúc chờ, đỉnh **~35 MiB** khi rclone tải lên song song 8 luồng 200 file 200 KB và một file 100 MB. Trần `GARAGE_MEM_LIMIT` 192m (8 GB) / 128m (2 GB) để dư cho bộ đệm khối và cache metadata khi số object lớn. `files-backup` (rclone) chỉ chạy một vòng shell lúc chờ; khi chép dùng 2 luồng, bộ đệm 4 MB: trần 128m / 96m. ClamAV (profile `clamav`) giữ cơ sở dữ liệu chữ ký trong RAM, ~1–1,5 GiB: chỉ hợp với máy 8 GB.

## Sau khi tối ưu

Cùng cách đọc như bảng trước. Hai dòng "chưa sửa keep-alive" là lượt đo trước khi thêm `KEEP_ALIVE_TIMEOUT` (mục 7).

| Cấu hình | web | api | worker | postgres | caddy | **Tổng** | CPU stack | req/s | p95 | lỗi |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 8 GB: 2 web + 2 api + worker, load | 80 / 287 / 183 ×2 | 80 / 256 / 254 ×2 | 75 / 86 / 87 | 67 / 223 / 71 | 14 / 23 / 22 | **316 / 875 / 617** | 492 % | 6.8 | 8.34s | 0.00% |
| 8 GB, stress | 181 / 747 / 339 ×2 | 253 / 291 / 275 ×2 | 87 / 94 / 95 | 69 / 203 / 97 | 22 / 40 / 39 | **612 / 1375 / 845** | 477 % | 14.5 | 16.56s | 0.00% |
| 8 GB, load (chưa sửa keep-alive) | 88 / 298 / 179 ×2 | 81 / 267 / 262 ×2 | 74 / 81 / 83 | 63 / 226 / 73 | 14 / 23 / 22 | **320 / 895 / 619** | 501 % | 5.9 | 10.31s | 0.27% |
| 8 GB, stress (chưa sửa keep-alive) | 183 / 484 / 264 ×2 | 262 / 292 / 287 ×2 | 83 / 92 / 92 | 70 / 177 / 92 | 22 / 36 / 30 | **620 / 1081 / 765** | 515 % | 8.5 | 28.26s | 0.16% |
| 2 GB: 1 web + 1 api (có outbox), load | 39 / 199 / 111 | 87 / 145 / 141 | — | 42 / 114 / 63 | 12 / 22 / 20 | **180 / 480 / 335** | 210 % | 10.4 | 5.99s | 0.00% |
| 2 GB, stress | 111 / 481 / 193 | 141 / 157 / 155 | — | 45 / 108 / 56 | 20 / 38 / 36 | **317 / 784 / 440** | 258 % | 11.3 | 17.71s | 0.00% |
| 2 GB, load (chưa sửa keep-alive) | 41 / 186 / 122 | 90 / 144 / 143 | — | 46 / 107 / 49 | 15 / 24 / 22 | **192 / 461 / 336** | 242 % | 5.0 | 11.99s | 0.00% |
| 2 GB, stress (chưa sửa keep-alive) | 129 / 335 / 162 | 143 / 155 / 153 | — | 45 / 100 / 52 | 22 / 36 / 34 | **339 / 626 / 401** | 270 % | 5.8 | 25.73s | 0.11% |

So sánh tổng (MiB, tổng đỉnh từng container, cùng phiên đo xen kẽ với cấu hình cũ):

| Hồ sơ, kịch bản | Đỉnh: trước → sau | Sau tải: trước → sau | Lỗi: trước → sau |
| --- | --- | --- | --- |
| 8 GB (2 web + 2 api + worker), load | 1 248–1 280 → **875–895** (−30 %) | 928–1 168 → **617–619** (−33 đến −47 %) | 0–0,04 % → **0 %** |
| 8 GB, stress | 1 555–1 571 → **1 081–1 375** | 1 171–1 248 → **765–845** (−35 %) | 0,13–0,51 % → **0 %** |
| 2 GB (1 web + 1 api; trước: + worker), load | 859–868 → **461–480** (−45 %) | 816–848 → **335–336** (−60 %) | 0–7 % → **0 %** |
| 2 GB, stress | 1 101–1 105 → **626–784** (−29 đến −43 %) | 892–927 → **401–440** (−54 %) | 0 % → **0 %** |

Đỉnh khi stress của web tỉ lệ với số trang render cùng lúc: lượt nào máy rảnh hơn (thông lượng cao hơn) thì đỉnh web cao hơn, nên khoảng "sau" khi stress rộng. Mọi lượt đều nằm dưới trần đã đặt.

## Cấu hình khuyến nghị

Hai khối trong [`infra/deploy/.env.example`](../infra/deploy/.env.example); compose mặc định bằng hồ sơ 8 GB, nên server có `.env` cũ vẫn chạy.

| | Máy nhỏ: 1–2 vCPU / 2 GB | Máy chuẩn: 4 vCPU / 8 GB |
| --- | --- | --- |
| Ví dụ | Oracle Always Free (A1, chia nhỏ), AWS t3.small | VPS 4 vCPU / 8 GB, Oracle A1 4 OCPU / 24 GB |
| web | 1 × (heap 384 MB, trần 640m) | 2 × (heap 512 MB, trần 768m) |
| api | 1 × trần 384m, **chạy luôn outbox** | 2 × trần 512m |
| worker | 0 | 1 × trần 256m |
| signaling, coturn | trần 96m, 64m | trần 256m, 128m |
| garage, files-backup (kho file) | trần 128m, 96m | trần 192m, 128m; ClamAV tuỳ chọn 1536m |
| pool mỗi module | api 6 | api 6, worker 3 |
| PostgreSQL | 80 kết nối, `shared_buffers` 128MB, trần 512m | 200 kết nối, `shared_buffers` 512MB, trần 2g |
| RAM stack đo được: load / stress (tổng đỉnh) | ~470 / 630–780 MiB | ~880 / 1 080–1 375 MiB |
| Tổng các trần | ~1,65 GB (web 640 + api 384 + PostgreSQL 512 + signaling 96 + coturn 64). Trần là mức tối đa, không phải mức dùng: số đo thực tế ~0,5–0,8 GB, phần còn lại cho hệ điều hành, Docker, Caddy | ~5,1 GB (phần còn lại là page cache cho PostgreSQL) |

Máy 2 GB nên bật thêm swap 1–2 GB (`fallocate -l 2G /swapfile && mkswap /swapfile && swapon /swapfile`, thêm vào `/etc/fstab`) để đỡ những lúc build/pull image.

Khi nào đổi:

- `docker stats` cho thấy web chạm gần `WEB_MEM_LIMIT` thường xuyên → tăng `WEB_HEAP_MB` (và trần) chứ không tăng bản sao.
- CPU web gần 100 % mỗi bản sao → thêm bản sao web (mỗi bản sao ~1 lõi) nếu còn vCPU.
- Lỗi `53300 too many clients` hoặc `The connection pool has been exhausted` → kiểm tra lại công thức pool ở mục 1 trước khi tăng `PG_MAX_CONNECTIONS`.

## Bộ giám sát

Profile `monitoring` (bật/tắt và cách dùng: [trien-khai.md](trien-khai.md#giám-sát-và-cảnh-báo)) thêm 10 container chạy thường trực (cộng `postgres-monitor-role` chạy xong rồi thoát) và Redis (luôn chạy, ~4–6 MiB). Đo trên stack production local (2 web + 2 api + worker, máy dev dùng chung như mục [Cách đo](#cách-đo)), 20 người dùng ảo mở trang công khai liên tục 3 phút, mọi request được trace (`OTEL_TRACES_RATIO=1`), lấy mẫu `docker stats` mỗi 5 s:

| Container | Mới khởi động | Đỉnh khi có tải | Trần mặc định (8 GB) | Ghi chú |
| --- | --- | --- | --- | --- |
| tempo | 21 | 144 | 384m | Trước khi đặt `GOMEMLIMIT`: 277 MiB sau ~20 phút, sát trần |
| alloy | 82 | 131 | 256m | OTLP + log mọi container + thăm dò từng container |
| grafana | 18 | 121 | 256m | Trước `GOMEMLIMIT`: 187 MiB |
| prometheus | 50 | 61 | 512m | Tăng theo số series và thời gian giữ (15 ngày) |
| loki | 11 | 57 | 384m | |
| cadvisor | 17 | 21 | 128m | Chế độ cgroup thô (`housekeeping_interval=30s`) |
| blackbox, alertmanager, node-exporter, postgres-exporter | 9–18 | 12–22 | 48–64m | |
| **Tổng bộ giám sát** | **~250** | **~600** | ~2,1 GB | Sau tải còn ~530 MiB |

- Ứng dụng khi bật OTel: không đo tách riêng; api 110–160 MiB, web 75–157 MiB trong lượt đo này, cùng khoảng với số đo không có OTel ở trên (chênh lệch nằm trong độ nhiễu của máy dùng chung). CPU của Alloy ~10 % một lõi khi có tải.
- **`GOMEMLIMIT`**: Go không tự đọc trần RAM của container nên Tempo/Grafana giữ heap tới gần trần; compose đặt `GOMEMLIMIT` (biến `*_GOMEMLIMIT`, ~70–75 % trần) cho tempo, loki, grafana, alloy, prometheus, giảm tổng ~840 → ~600 MiB.
- **cAdvisor**: tích hợp Docker của cAdvisor (v0.52–0.53) không đọc được container khi Docker dùng *containerd image store* (mặc định ở Docker Desktop và Docker Engine 29 cài mới), nên chạy ở chế độ cgroup thô (không cần socket Docker); tên container và service lấy từ Alloy (rule `konnyaku:container_*`). Cảnh báo thiếu bản sao không dựa vào cAdvisor mà dựa vào thăm dò `/health` từng container.

**Máy 8 GB**: chạy chung được (stack ~0,9–1,4 GB + giám sát ~0,6 GB).

**Máy 2 GB**: ứng dụng đã dùng ~0,5–0,8 GB; thêm ~0,6 GB giám sát là quá sát. Hai cách:

1. **VPS giám sát riêng (khuyến nghị)**, 1 vCPU / 1–2 GB: chạy Grafana, Prometheus, Loki, Tempo, Alertmanager ở đó; trên máy ứng dụng chỉ giữ các thành phần thu thập (alloy, node-exporter, cadvisor, postgres-exporter, blackbox: ~200 MiB). Nối hai máy bằng mạng riêng của nhà cung cấp hoặc WireGuard (không mở OTLP / remote-write ra Internet): Alloy đổi `tempo:4317`, `prometheus:9090` thành địa chỉ máy giám sát; Prometheus ở máy giám sát scrape các exporter qua mạng riêng (hoặc để Alloy scrape và remote-write). Cùng máy đó chạy được Uptime Kuma và GlitchTip, và vẫn báo động khi máy ứng dụng chết hẳn.
2. **Chạy chung với trần nhỏ**: khối "Small machine" trong `.env.example` (trần ~1,1 GB tổng, giữ dữ liệu ngắn hơn, `OTEL_TRACES_RATIO=0.2`) và bật swap; chấp nhận Grafana chậm khi truy vấn dài.

## arm64

CI build và chạy thử image trên runner arm64 thật (`ubuntu-24.04-arm`); `smoke-test.sh` nay in RAM lúc rảnh của api và web ngay sau khởi động trên từng kiến trúc:

| Kiến trúc | api lúc rảnh | web lúc rảnh | Nguồn |
| --- | --- | --- | --- |
| amd64 | ~50 MiB | ~53 MiB | `smoke-test.sh` trên máy dev |
| arm64 | dòng `memory kk-smoke-…` | dòng `memory kk-smoke-…` | log bước *Smoke test* của job `images (arm64)` trong CI của PR |

Chưa đo có tải trên arm64 (máy dev là amd64; giả lập QEMU làm sai số đo). Runtime .NET và V8 trên arm64 dùng bộ nhớ tương đương amd64, nên hai hồ sơ trên áp dụng được cho Oracle Ampere A1. Khi có máy arm64 thật, chạy lại `measure.sh` theo mục [Cách đo](#cách-đo) rồi bổ sung bảng.

## Rust: có nên tách service không?

Tiêu chí của dự án ([06-evolution-strategy.md §6.6](06-evolution-strategy.md#66-khi-nào-tách-một-module--tiêu-chí-định-lượng)) yêu cầu tín hiệu đo được. Đặt các ứng viên cạnh số đo:

| Ứng viên | RAM hiện tại | Rust tiết kiệm được (ước lượng) | Công sức, rủi ro | Kết luận |
| --- | --- | --- | --- | --- |
| **web** (Next.js, SSR React) | 110–250 MiB / bản sao sau tối ưu; ~2/3 CPU của stack | Không áp dụng: React SSR, next-intl, Server Components cần JavaScript runtime | Viết lại toàn bộ giao diện sang framework khác | Không. Giảm CPU bằng cache trang công khai (ISR/`revalidate`) hoặc cache ở Caddy |
| **api** (.NET, 11 module, ~7 400 dòng C# + 1 750 dòng test) | ~120–150 MiB / bản sao | Một service axum + sqlx tương đương thường 20–40 MiB: **~100 MiB mỗi bản sao** (~200 MiB ở hồ sơ 8 GB, ~100 MiB ở 2 GB) | Viết lại toàn bộ nghiệp vụ, outbox/inbox, RLS, hash mật khẩu tương thích ASP.NET Identity, migration; mất ArchitectureTests và test tích hợp hiện có. Ước lượng 6–10 tuần một người, rủi ro hồi quy cao | Không. Cấu hình đã lấy lại nhiều RAM hơn mức này với ~0 rủi ro |
| **worker** (outbox) | ~80 MiB | Không tách được riêng: handler là nghiệp vụ .NET của từng module, chạy trong process | Phải viết lại handler = viết lại module | Không. Máy nhỏ dùng `API_RUNS_WORKER=true` (tiết kiệm ~80 MiB, không đổi code nghiệp vụ) |
| Một module tải cao (ví dụ đăng nhập PBKDF2, form công khai) | Không đo được phần riêng vượt 40 % process | — | Tách module = playbook §6.7, cần HTTP giữa các module | Chưa đạt tiêu chí "tải khác biệt" |
| Signaling WebRTC | ~1 MiB rảnh, ~210 KiB mỗi người kết nối (mục 9) | — | Đã viết bằng Rust (phòng học P2P) | Đúng chỗ: service mới, không có nghiệp vụ cũ, nhiều kết nối dài |

Tóm lại: RAM thừa nằm ở **cấu hình** (heap Node, pool kết nối, container worker), không nằm ở ngôn ngữ. Rust đáng dùng cho **service mới, độc lập, nhiều kết nối đồng thời** (như signaling), không đáng để viết lại phần nghiệp vụ đang chạy.

Xem xét lại khi một trong các điều sau xảy ra:

- Cần ≥ 4 bản sao API và RAM API chiếm > 40 % máy.
- Một endpoint chiếm > 40 % CPU API trong số đo thật (ví dụ tra cứu lịch công khai lượng lớn) và tách được theo đường cắt module.
- Xuất hiện workload mới không có nghiệp vụ cũ, cần nhiều kết nối dài (thông báo realtime, gateway WebSocket): viết thẳng bằng Rust từ đầu.

## Việc nên làm tiếp

- **CPU của web**: một trang SSR tốn ~100–150 ms CPU; trang công khai (`/vi`, `/vi/dang-ky`) giống nhau cho mọi khách, nên cache được (ISR theo tenant hoặc cache ở Caddy) → giảm phần lớn CPU từ khách vãng lai.
- **Cache tenant ở API**: `ResolveHostAsync` không chống "cache stampede" khi hết hạn; dùng `HybridCache` hoặc khóa theo key để chỉ một request truy vấn.
- **Caddy với một bản sao web**: `fail_duration 30s` biến mọi lỗi kết nối lẻ thành 30 s gián đoạn khi chỉ có một upstream (mục 7). Đã loại nguyên nhân chính bằng `KEEP_ALIVE_TIMEOUT`; nên cân nhắc giảm `fail_duration` hoặc bỏ passive health check khi `WEB_REPLICAS=1`.
- Đo coturn khi relay thật; nếu lớp học đông hơn dự kiến, hạ bộ đệm WebSocket của signaling (mục 9).
- Đo lại trên máy arm64 thật và máy 2 GB thật (không dùng chung với việc khác) để có độ trễ đáng tin.
