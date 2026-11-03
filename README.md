# Minecraft Session Tool — Session Garden

Giao diện desktop tông tím và hồng pastel, linh vật pixel cây nhỏ, hỗ trợ tiếng Việt.

## Cách dùng

1. Mở MinecraftSessionTool.exe.
2. Kéo thả một file cookie Netscape vào khung, hoặc nhấn **Chọn file**.
3. Ứng dụng tự động tạo phiên ngay khi nhận file. Có thể nhấn **Hủy** khi đang xử lý.
4. Khi thành công, thông tin người chơi xuất hiện và token được sao chép tự động. Có thể sao chép lại token hoặc lệnh chạy bằng hai nút dưới phần kết quả.
5. Kết quả chỉ hiển thị trong ứng dụng và có thể sao chép; không xuất file hoặc tạo thư mục output.

Token được che khi mới tạo; chọn **Hiện** để xem. Chọn file khác sẽ xóa kết quả đang hiển thị.

Có thể kéo thả file trực tiếp lên biểu tượng .exe, hoặc đặt cookies.txt cạnh .exe để ứng dụng chọn sẵn khi mở. Ứng dụng tự động tạo phiên khi mở với file có sẵn.

## Mã nguồn và đóng gói

Bản Windows 64-bit chạy độc lập, không cần cài .NET riêng. Mã nguồn ở **src**; cần .NET SDK 10 để chỉnh sửa và đóng gói lại:

    dotnet publish .\src\MinecraftSessionTool.csproj -p:PublishProfile=Windows

Kết quả ở **publish\MinecraftSessionTool.exe**. Đóng ứng dụng rồi thay file .exe tại thư mục chính bằng bản vừa đóng gói.

Luồng đăng nhập và định dạng file kết quả được giữ từ bản gốc. Giao diện bổ sung tiến trình, hủy thao tác và thông báo lỗi ngay trong cửa sổ.


