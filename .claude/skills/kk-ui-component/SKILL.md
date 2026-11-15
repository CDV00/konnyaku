---
name: kk-ui-component
description: Thêm component animate-ui hoặc shadcn/ui vào package dùng chung @konnyaku/ui, hoặc tạo khối giao diện riêng trong src/kk; xử lý import cn, exports, Tailwind v4 và lint cho code vendored. Dùng khi cần component giao diện chưa có trong frontend/packages/ui.
---

# Component trong `@konnyaku/ui`

Cấu trúc `frontend/packages/ui/src`:

| Thư mục | Nội dung | Sửa được không |
| --- | --- | --- |
| `components/animate-ui/**` | Component animate-ui (Radix + motion) chép bằng shadcn CLI | Hạn chế, giữ gần bản gốc |
| `components/ui/**` | Component shadcn/ui | Hạn chế |
| `kk/**` | Khối riêng của Konnyaku (shell, blocks, field, logo, providers) | Có, đây là nơi viết code mới |
| `styles/globals.css` | Token màu sáng/tối (oklch), `@theme inline`, `@source` | Có |

## Thêm component từ registry

```bash
cd frontend/packages/ui && npx shadcn@latest add @animate-ui/<ten-component>
```

Registry `@animate-ui` khai báo trong `components.json`. Component shadcn thường: `npx shadcn@latest add <ten>`.

Sau khi thêm:

1. Kiểm tra mọi import `cn` trỏ về `@konnyaku/ui/lib/utils`. CLI đôi khi sinh import sai hoặc thêm package `cn` vào `package.json`: sửa import và xoá dependency đó.
2. Dependency mới mà CLI thêm (ví dụ `@radix-ui/*`) phải nằm trong `packages/ui/package.json`. Chạy `npx -y pnpm@10.34.9 install` ở `frontend/`.
3. **Không** nâng `tailwindcss` hay `@tailwindcss/postcss` khỏi `4.1.18` (Node 20.18 không chạy được bản 4.3+).
4. Code vendored được miễn một số rule lint (`eslint.config.mjs`). File mới nằm ngoài các thư mục đó thì phải qua lint đầy đủ.
5. Nếu cần sửa component vendored (ví dụ thêm prop `closeLabel` cho `sheet.tsx` để dịch nhãn), sửa tối thiểu và ghi chú bằng comment ngắn.

## Dùng trong app

Import theo đường dẫn exports: `@konnyaku/ui/components/animate-ui/components/radix/tabs`, `@konnyaku/ui/kk/blocks`, `@konnyaku/ui/lib/utils`. Không cần đăng ký thêm: `exports` của package dùng wildcard, app đã có `transpilePackages`.

Chữ hiển thị (nhãn, aria-label) truyền từ app qua props, lấy từ i18n; component trong `kk/` không chứa chữ cứng.

## Lưu ý hiệu ứng

- Component dùng `motion` (Progress, CountingNumber) có thể hiện sai trạng thái khi render phía server; với giá trị tĩnh dùng CSS thường (ví dụ thanh sĩ số trong bảng lớp).
- Tôn trọng `prefers-reduced-motion` nếu tự viết animation.

## Kiểm tra

`cd frontend && npx -y pnpm@10.34.9 lint && npx -y pnpm@10.34.9 typecheck && npx -y pnpm@10.34.9 build`
