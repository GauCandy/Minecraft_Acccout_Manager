# 🍪 Minecraft Account Manager

Cho bạn bè mượn tài khoản Minecraft **mà không phải đưa mật khẩu Microsoft**.

Bạn giữ tài khoản, chỉ gửi đi **mã NFA** (Minecraft Access Token): đủ để vào game, không đổi được mật khẩu, không vào được email, và tự hết hạn.

## Tính năng

- **Thêm tài khoản 3 cách:** **🔑 Đăng nhập Microsoft** bằng mã thiết bị, **🍪 Nhập cookie** (dán hoặc chọn file `.txt` / `.json`), hoặc **🔁 Nhập refresh token** (mỗi dòng một token, nhập nhiều tài khoản một lượt).
- **📋 Quản lý nhiều tài khoản:** tích chọn để làm mới token, sao chép NFA hàng loạt.
- **🏷 Tag & bộ lọc** để phân loại tài khoản.
- **🌐 Proxy đa luồng:** giúp làm mới mã NFA nhanh hơn. N proxy thì làm mới N tài khoản cùng lúc thay vì lần lượt từng cái.

## Cách dùng

1. Mở `MinecraftAccountManager.exe`.
2. Bấm **🔑 Đăng nhập Microsoft** và nhập mã trên trang Microsoft.
3. Bấm **📋 Sao chép Token** rồi gửi cho người mượn. Hết hạn thì bấm **⚡ Làm mới Token**.

> ⚠️ Chỉ gửi mã NFA. Không chia sẻ thư mục `~/minecraft_cookie_manager/`, vì trong đó có cookie và refresh token.

## Điều khoản sử dụng và miễn trừ trách nhiệm

Trước khi đăng nhập, nhập cookie hoặc chia sẻ token, vui lòng đọc [Điều khoản sử dụng và miễn trừ trách nhiệm](DIEU-KHOAN-SU-DUNG.md). Chỉ sử dụng tài khoản được chủ sở hữu cho phép và phù hợp với quy định của dịch vụ liên quan. Cookie và token là thông tin xác thực nhạy cảm; việc chia sẻ có thể dẫn đến mất quyền truy cập hoặc lộ dữ liệu. Phần mềm được cung cấp theo hiện trạng, với giới hạn trách nhiệm trong phạm vi pháp luật cho phép.

## Build

```powershell
dotnet publish .\src\MinecraftSessionTool.csproj -p:PublishProfile=Windows
```

Cảm ơn **JerTran (Discord)** đã tài trợ dự án ♡ · Giấy phép [AGPL-3.0](LICENSE)
