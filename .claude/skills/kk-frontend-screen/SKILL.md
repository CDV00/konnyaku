---
name: kk-frontend-screen
description: Thêm hoặc sửa màn hình, form, bảng, chuỗi giao diện trong các app Next.js của Konnyaku (site, learn, teach, ops) — server component, requireMe, i18n vi/en, khối @konnyaku/ui, FormSheet/ActionForm và relay của ops. Dùng khi làm việc trong frontend/apps/** hoặc frontend/packages/{session,i18n}.
---

# Màn hình frontend

Bốn app Next.js 16 (App Router) chạy chung một host nhờ multi-zone: `site` (cổng 4000) rewrite `/learn`, `/teach`, `/ops` sang app tương ứng (4001–4003, `basePath` riêng; xem `apps/site/proxy.ts`). Link trong một app viết tương đối với `basePath` của app đó (`/classes`, không phải `/ops/classes`).

## Trang đọc dữ liệu (server component)

1. `const { me, token } = await requireMe("staff" | "learn")` từ `@konnyaku/session` — tự chuyển về `/login` nếu chưa đăng nhập.
2. Gọi API: trong `ops` dùng `getJson<T>(path, token, fallback)` ở `apps/ops/app/lib.ts`; nơi khác `apiFetch(path, { token })`. Gọi song song bằng `Promise.all`.
3. Chữ: `const t = await getTranslations("<namespace>")`; ngày giờ/số: `getFormatter()` (múi giờ của trung tâm đã cấu hình trong `i18n/request.ts`, đừng tự format bằng `toLocaleString`). Tên đa ngôn ngữ (`LocalizedString`) hiển thị bằng `localized(value, locale)`.
4. Bố cục bằng khối có sẵn trong `@konnyaku/ui/kk/blocks`: `PageHeader` (title, description, actions), `Panel`, `StatCard`, `EmptyState`, `StatusBadge` (tone lấy từ `statusTone` trong `ops/app/lib.ts`). Bảng: `@konnyaku/ui/components/ui/table`. Icon: `lucide-react`.

## Form ghi dữ liệu

- `ops`: bọc form trong `FormSheet` (ngăn kéo bên phải) + `ActionForm api="<module>/<path>"` (gửi qua `/ops/api/relay/*`; tên field dạng `name.vi` thành object lồng; checkbox `CheckRow`). Form tự đóng khi thành công, `showResult` để giữ mở và hiện kết quả (mật khẩu tạm). Nút một thao tác: `ActionButton`.
  - **Bắt buộc** thêm regex đường dẫn vào allowlist `apps/ops/app/api/relay/[...path]/route.ts`, nếu không relay trả 404.
  - Ô nhập: `Field` + `Input`/`NativeSelect` từ `@konnyaku/ui/kk/field` và `@konnyaku/ui/components/ui/input`.
- `site`/`learn`/`teach`: client component (`"use client"`) gọi route handler trong `app/api/**/route.ts` của app; route handler lấy token bằng `sessionToken(realm)` rồi `apiFetch`.
- Lỗi API trả `{ code }`; hiển thị `t(\`errors.${code.replaceAll(".", "_")}\`)` với fallback `errors.unknown`.

## i18n (bắt buộc)

- Không viết chữ trong JSX: ESLint `react/jsx-no-literals` báo lỗi. Ký tự cho phép: `·`, `—`, `/`, `|`.
- Thêm key vào **cả** `frontend/packages/i18n/messages/vi/apps.json` và `en/apps.json` (cùng cấu trúc; kiểu `Messages` suy ra từ `vi` nên thiếu key `vi` sẽ lỗi typecheck, thiếu `en` sẽ hiện key thô khi chạy tiếng Anh).
- Dùng ICU: `{count, plural, other {# buổi}}`, `{amount}`; rich text bằng `t.rich`.
- Mã lỗi mới từ backend: key `errors.<module>_<code>` trong namespace của màn hình (`admin.errors` cho ops).

## Giao diện

- Màu dùng token (`bg-primary`, `text-muted-foreground`, `bg-success/10`…), không mã màu cứng; phải đẹp cả sáng và tối.
- Responsive tới 375px: không cuộn ngang; ẩn cột phụ bằng `hidden lg:table-cell`.
- Hiệu ứng: dùng component animate-ui có sẵn (`Fade`, `CountingNumber`, `Tabs`, `Sheet`, `ToggleGroup`). Cần component mới → skill `kk-ui-component`.
- Học viên/giáo viên dùng `ZoneLayout` (`@konnyaku/session/zone-layout`), ops dùng `SidebarShell`; thêm mục menu ops ở `apps/ops/app/layout.tsx` (mảng `sections`) + key `ops.nav.<x>` và `ops.hint.<x>`.

## Kiểm tra

```bash
cd frontend && npx -y pnpm@10.34.9 lint && npx -y pnpm@10.34.9 typecheck
```

Sau đó mở trang trên `http://demo.localhost:4000/...` (xem `docs/chay-local.md`), thử cả sáng/tối và khổ điện thoại.
