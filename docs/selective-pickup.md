# Chọn một phần thiết bị trong tủ

Đăng ký phiên → chọn món trên web → tạo QR → mở cửa → quét RFID các món đã chọn → lấy đồ và đóng cửa. Tủ có mic, điều khiển điều hòa/TV, chìa khóa; danh sách cho mượn lấy từ thiết bị CABINET có tình trạng TOT và thẻ RFID đã gắn. TV, điều hòa và tài sản ROOM không xuất hiện.

Mỗi checkbox là một món vật lý/UID, nên có thể chọn 1 trong nhiều mic cùng loại. Danh sách borrowing_equipment và ảnh chụp RFID của QR chỉ chứa các món đã chọn; không giảm quantity của kho/tủ khi mượn. Không có schema migration và không sửa dữ liệu thật khi triển khai code.

Nhận đồ thiếu/sai UID: phiên chưa chuyển sang IN_USE; hiện RECEIVE_RFID_FAILED để cấp lại QR và quét lại khi cửa mở. Cửa vật lý vẫn đóng/khóa và firmware được ACK bình thường để có thể mở lại. Quét ngoài phiên/cửa đóng không tính cho phiên.

Trả đồ: dùng nguyên danh sách UID đã chọn ở lượt nhận, không lấy thẻ của các món ở lại trong tủ. Xác nhận trả thông thường yêu cầu MATCH RFID; AI không thay thế điều kiện này. Nút xác nhận thủ công của admin vẫn là ngoại lệ có audit, không giả lập RFID/camera PASS.

QR cũ chưa có danh sách chọn phải được tạo lại. Phiếu đang mượn trước cập nhật giữ snapshot cũ; không tự sửa lịch sử. Firmware v14 đã gửi các lượt quét cho cả nhận và trả, không cần đổi giao thức hay nạp firmware mới.

Kiểm tra: dotnet build tests/SelectivePickup/SelectivePickup.csproj -o .codex-temp/selective-tests -p:UseAppHost=false; dotnet .codex-temp/selective-tests/SelectivePickup.dll. Không mở DB; giao dịch SQL chưa được chạy trên dữ liệu thật trong lần kiểm thử này.
