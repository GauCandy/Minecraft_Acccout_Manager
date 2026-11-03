# Nhật ký và kiểm tra proxy

## Nhật ký riêng

Chọn **Nhật ký** ở thanh bên để xem các bước xử lý của phiên đang mở. Khung nhật ký cũ trong trang Cookie đã được chuyển sang đây.

Log ghi thời gian, bước thực hiện, kết quả, loại lỗi, HTTP status và thời gian xử lý khi có. Tệp báo cáo chỉ có phiên bản app/.NET, kiến trúc hệ điều hành và mã phiên ngẫu nhiên để đối chiếu lỗi. Không ghi token, cookie, tên tài khoản, UUID, IP/địa chỉ proxy, tài khoản/mật khẩu proxy, email, đường dẫn cá nhân, exception message hoặc stack trace thô.

Các sự kiện log được định nghĩa sẵn bằng enum và số đã kiểm tra. Nội dung tự do từ cookie, dịch vụ, proxy hoặc lỗi không được đưa vào tệp log. Đây là cách tránh để dữ liệu nhạy cảm lọt qua những chuỗi không khớp một biểu thức che token.

Log được lưu tự động tại `~/minecraft_cookie_manager/logs/`, giữ tối đa 10 phiên gần nhất và 2.000 sự kiện mỗi phiên. Nếu app gặp lỗi đóng đột ngột, các bước đã ghi vẫn nằm trong file của phiên đó. Nếu không có quyền ghi, app vẫn giữ log trong bộ nhớ để xem và xuất.

- **Xuất log báo lỗi**: chọn nơi lưu một tệp .txt đã loại bỏ dữ liệu cá nhân để tự đính kèm vào issue.
- **Thư mục log**: mở các phiên đã lưu, có thể lấy phiên trước nếu app vừa bị đóng lỗi.
- **Xóa phiên này**: xóa nội dung phiên hiện tại, không xóa kho cookie/proxy và không xóa tệp báo cáo đã xuất.

App không tự gửi log, tạo issue hoặc tải báo cáo lên mạng. Khi báo lỗi, dùng tệp từ **Xuất log báo lỗi**; nút **Xuất Cookie** là chức năng khác và chứa dữ liệu đăng nhập riêng của tài khoản.

## Kiểm tra proxy

Đã sửa lỗi kết quả kiểm tra bị mất vì danh sách được tải lại trước khi lưu. Kiểm tra hàng loạt lấy bản chụp danh sách, tối đa 8 kết nối đồng thời và lưu từng kết quả trước khi làm mới giao diện. Kết quả cũ không ghi đè proxy đã đổi hoặc đưa proxy đã xóa trở lại.

Đây là kiểm tra yêu cầu HTTPS qua proxy đến Minecraft, không phải ping ICMP và không dùng cookie/token. HTTP 401 từ endpoint yêu cầu đăng nhập nghĩa là đã đi được đến dịch vụ, không tự động kết luận proxy chết. Lỗi xác thực proxy, bị chặn, giới hạn, timeout và lỗi kết nối được phân biệt; rê chuột vào trạng thái để xem gợi ý an toàn.

Proxy không có tiền tố được hiểu là SOCKS5. Có thể nhập trực tiếp host:port:user:pass. Nếu nhà cung cấp đưa HTTP, thêm http:// trước địa chỉ (dạng URL dùng http://user:pass@host:port). Các mật khẩu chứa dấu ':' hoặc '@' trong dạng host:port:user:pass được giữ đúng. Danh sách hiển thị không lộ tài khoản và mật khẩu proxy.

## Kiểm tra bản sửa

90 kiểm tra tự động đã đạt, gồm dữ liệu nhạy cảm giả lập trong exception/progress/UI, file log và báo cáo, phân loại HTTP, hủy và timeout, parser proxy, lưu kết quả, giới hạn log, giữ phiên và bố cục 640/960/1160 px. Không đọc cookie/proxy thật và không gọi mạng bằng dữ liệu người dùng. Kết nối thực tế vẫn phụ thuộc proxy, giao thức và thông tin xác thực của nhà cung cấp.

