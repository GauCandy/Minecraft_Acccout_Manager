# Minecraft Cookie Manager — v1.0.1 🍪

Ứng dụng quản lý và lưu trữ cookie tài khoản Minecraft trên Windows, giao diện tông màu pastel thanh lịch với biểu tượng bánh quy pixel art đặc trưng của Minecraft.

## ✨ Tính năng nổi bật trong bản 1.0.1

1. **Quản lý đa tài khoản & Cookie Store**:
   - Tự động lưu trữ danh sách tài khoản và cookie vào thư mục riêng: `~/minecraft_cookie_manager/accounts.json`.
   - Hiển thị Skin Avatar Minecraft thật của tài khoản (tải từ dịch vụ skin chính thức).
   - Hỗ trợ xuất cookie ra định dạng Netscape (`.txt`) và JSON (`.json`).

2. **Xoay vòng Proxy (Proxy Pool & Rotation)**:
   - Thêm danh sách proxy HTTP/HTTPS/SOCKS5 (dạng `host:port` hoặc `host:port:user:pass`).
   - Tự động xoay vòng proxy theo cơ chế Round-robin khi lấy NFA token, giúp giảm thiểu và chống bị Microsoft gắn cờ IP / spam limit.

3. **Hỗ trợ toàn diện định dạng Cookie**:
   - Nhận diện cả định dạng văn bản Netscape (`.txt`) lẫn định dạng JSON (`.json`) từ các extension như Cookie-Editor, EditThisCookie,...

4. **Script lấy Cookie tự động từ Chrome**:
   - Kèm theo công cụ `lay_cookie.bat` / `get_cookie.py` giúp mở trình duyệt Chrome, đăng nhập và tự động lưu/sao chép cookie vào clipboard.

5. **Làm mới Token với 1 click (⚡ Làm mới Token)**:
   - Dễ dàng lấy lại Access Token Minecraft mới nhất từ cookie đã lưu.

6. **Biểu tượng Cookie Minecraft sắc nét**:
   - Texture bánh quy Minecraft chuẩn pixel-perfect.

---

## 💖 Lời cảm ơn đặc biệt (Credits)

- **Special thanks to JerTran (Discord)** — Người tiên phong tài trợ đặt nền móng cho dự án.

---

## 🚀 Cách sử dụng

1. Mở file `MinecraftCookieManager.exe`.
2. Thêm cookie tài khoản bằng một trong các cách:
   - Kéo và thả file cookie (`.txt` hoặc `.cookies`) vào khung nhận diện.
   - Bấm nút **Chọn file** hoặc **+ Thêm Cookie mới**.
   - Bấm nút **Dán Clipboard** nếu bạn đã copy cookie vào bộ nhớ tạm.
3. Ứng dụng sẽ tự động xác thực với Microsoft/Xbox/Mojang và lưu tài khoản vào danh sách.
4. Chọn bất kỳ tài khoản nào ở danh sách bên trái để lấy **Access Token** hoặc **Lệnh chạy game** (Launch arguments).

---

## 🛠 Đóng gói từ mã nguồn

Ứng dụng chạy độc lập (Self-contained, single-file), không yêu cầu cài trước .NET:

```powershell
dotnet publish .\src\MinecraftSessionTool.csproj -p:PublishProfile=Windows
```

File thực thi được tạo tại: `publish\MinecraftCookieManager.exe`.

## Cập nhật trong ứng dụng

Bấm nút cập nhật màu cam để xem nội dung thay đổi, tải và mở lại bản mới. Xem cách phát hành và dùng chế độ xem thử tại [CAP-NHAT.md](CAP-NHAT.md). Bản chạy độc lập đã bật nén, khoảng 64,9 MB.

