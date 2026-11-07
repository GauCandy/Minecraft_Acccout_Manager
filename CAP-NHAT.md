# Cập nhật Minecraft Account Manager

## Người dùng

Khi mở app, app kiểm tra bản phát hành mới trên GitHub GauCandy/Minecraft_Acccout_Manager. Nếu có phiên bản cao hơn, nút cập nhật màu cam hiện ở bên phải thanh tiêu đề. Bấm nút để xem nội dung thay đổi, sau đó chọn **Cập nhật và mở lại**.

App tải file .exe, kiểm tra dung lượng, SHA256 và phiên bản trong file, rồi thay bản đang chạy và mở lại. Cookie và proxy trong kho của người dùng không bị thay đổi. Bản .exe trước đó được giữ cạnh app với đuôi **.previous**; nếu quá trình thay file hoặc mở lại thất bại, app cố gắng mở lại bản cũ.

Nếu bản phát hành thiếu file hoặc thông tin kiểm tra phù hợp, app hiển thị nút mở trang tải thủ công. Mất mạng không ngăn dùng các tính năng khác.

Để xem trước: bấm liên tiếp **5 lần vào số phiên bản trên thanh tiêu đề** (mỗi lần cách nhau dưới 2 giây), sau đó bấm nút cập nhật màu cam và **Chạy thử cập nhật**. Chế độ này không tải, cài hoặc khởi động lại app. Bấm số phiên bản 5 lần nữa để tắt xem thử và về trạng thái thật. Chức năng này được giấu dưới dạng easter egg, không có nút trong bảng tác giả.

## Phát hành bản mới

1. Sửa **Version** trong `src/MinecraftSessionTool.csproj`, ví dụ `1.0.2`. Phiên bản giao diện và kiểm tra cập nhật tự lấy từ thông tin này.
2. Đóng gói:

       dotnet publish .\src\MinecraftSessionTool.csproj -p:PublishProfile=Windows

3. Tạo GitHub Release tại https://github.com/GauCandy/Minecraft_Acccout_Manager/releases với tag khớp phiên bản, ví dụ **v1.0.2**. Thêm ghi chú thay đổi để hiện trong app.
4. Đổi tên bản đóng gói từ `publish/MinecraftCookieManager.exe` thành **MinecraftAccountManager.exe**, rồi đính kèm vào mục Assets, rồi phát hành release ở trạng thái thông thường (không phải draft hoặc pre-release). Giữ đúng tên file. Tên cũ MinecraftSessionTool.exe cũng được hỗ trợ.
5. Không tải nhầm file .exe trong src/bin: đó có thể là bản cần các file phụ. Dùng đúng file .exe chạy độc lập trong publish.

GitHub cung cấp mã SHA256 cho asset sau khi tải lên. App dùng mã này và kiểm tra phiên bản bên trong file trước khi cài. Tag cao hơn nhưng file vẫn mang phiên bản cũ sẽ bị từ chối để tránh cập nhật sai.

Bản chạy độc lập được nén, vẫn kèm .NET/WPF nên không cần người dùng cài runtime riêng. File .exe hiện khoảng 64,9 MB (Windows hiển thị khoảng 61,9 MiB), giảm từ khoảng 140 MB. Khi chạy, .NET giải nén bộ chạy vào bộ nhớ đệm của Windows; con số trên là dung lượng file phát hành, không phải toàn bộ dung lượng bộ nhớ đệm.

## Kiểm tra bản sửa

Đã kiểm tra parser release, không hạ phiên bản, bỏ qua draft/pre-release, xác minh SHA256 và phiên bản file, file tải thiếu, hủy tải, thay file và giữ bản trước, rollback khi khởi động lỗi, từ chối parent không khớp, và giao diện xem thử. Các bài kiểm tra dùng file mẫu trong thư mục riêng và mô phỏng mạng/khởi động lại; không dùng cookie hay thay bản cài của người dùng. Bản phát hành thực tế mới hơn chưa có tại thời điểm kiểm tra, nên chưa thử cài từ một release GitHub thật mới hơn.

