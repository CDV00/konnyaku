# konnyaku-signaling

Signaling của **phòng học có sẵn** (provider `Konnyaku` trong module `Workspace`). Trình duyệt gửi hình/tiếng trực tiếp cho nhau (WebRTC P2P mesh); service này chỉ giúp họ tìm thấy nhau:

- kiểm vé (JWT HS256, `aud=konnyaku-rtc`, hạn 2 phút) do API cấp sau khi kiểm quyền vào lớp;
- giữ ai đang ở phòng nào (trong bộ nhớ, một phòng cho mỗi buổi của mỗi tenant), giới hạn chỗ, giữ một chỗ cho giáo viên;
- chuyển tiếp SDP/ICE và trạng thái mic/camera/màn hình;
- báo `joined` / `left` / `ended` về API (`POST /api/workspace/internal/rooms/events`, header `X-Rtc-Token` ký bằng cùng secret), thử lại khi API lỗi;
- tự đóng phòng trống sau giờ kết thúc buổi, hoặc phòng còn người sau `ROOM_GRACE_MINUTES`.

## Cấu hình (biến môi trường)

| Biến | Mặc định | |
| --- | --- | --- |
| `SIGNALING_SECRET` | bắt buộc, ≥ 32 byte | Trùng `Rtc:SignalingSecret` của API |
| `API_URL` | không đặt = không báo | Ví dụ `http://api:8080` |
| `ROOM_CAPACITY` | 6 | Số người mỗi phòng, gồm giáo viên |
| `ROOM_GRACE_MINUTES` | 30 | |
| `LISTEN_ADDR` | `0.0.0.0:5100` | |
| `RUST_LOG` | `info` | |

Đường dẫn: `GET /rtc/ws` (WebSocket), `GET /rtc/health`, `GET /health`. `konnyaku-signaling healthcheck` kiểm tra chính nó (image distroless không có shell).

## Giao thức

Mỗi frame là một object JSON có `type`.

| Chiều | `type` | Nội dung |
| --- | --- | --- |
| client → | `join` | `ticket`, `state` (`audio`, `video`, `screen`); phải là tin đầu tiên, trong 10 giây |
| client → | `signal` | `to` (id peer), `data` (SDP/ICE, chuyển nguyên) |
| client → | `state` | `audio`, `video`, `screen` |
| client → | `end` | chỉ vai trò `host` |
| client → | `leave` | |
| ← server | `welcome` | `self`, `peers[]`, `capacity` |
| ← server | `peer-joined` / `peer-left` | `peer` / `id` |
| ← server | `signal` | `from`, `data` |
| ← server | `state` | `id`, `state` |
| ← server | `ended` | rồi đóng kết nối |
| ← server | `error` | `code`: `invalid_ticket`, `ticket_expired`, `room_full`, `room_ended`, `replaced`, `not_allowed`, `bad_message` |

Người mới vào gửi offer tới từng người đã có; bật/tắt mic, camera, chia sẻ màn hình chỉ thay track nên không cần đàm phán lại.

## Phát triển

```bash
cargo fmt --check && cargo clippy --all-targets -- -D warnings && cargo test
```

Không cài Rust thì chạy trong Docker: `docker run --rm -v "$PWD:/src" -w /src rust:1.100-slim-bookworm sh -c "rustup component add clippy rustfmt && cargo test"`.
