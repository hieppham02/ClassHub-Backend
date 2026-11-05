# Camera và gắn RFID trong popup tủ

Bấm tên tủ ở trang quản lý. Tổng quan hiển thị cảm biến/kết nối/firmware, không hiển thị Bộ nhớ. Tab **Thiết bị & RFID** dùng để cấp thiết bị từ kho:

1. Cập nhật ESP32 lên firmware classhub-rfid-enroll-v13. Wi-Fi, MQTT và đồng bộ thời gian phải sẵn sàng.
2. Mở tab Thiết bị & RFID, đưa thẻ vào đầu đọc của đúng tủ.
3. Bấm **Chọn thẻ này**. Thẻ đã chọn giữ nguyên khi có lượt quét khác.
4. Chọn thiết bị còn trong kho. Bấm xác nhận sẽ chuyển **1** thiết bị và lưu UID.
5. Kiểm tra danh sách Đã gắn trong tủ. Không tự gửi lại yêu cầu nếu mạng ngắt giữa lúc lưu.

Thẻ vừa quét có hiệu lực 2 phút tính từ thời gian quét. Firmware cũ vẫn gửi telemetry bình thường nhưng không dùng được quy trình chọn thẻ mới. Thẻ đã gắn, tủ offline, tủ có phiên mượn/trả, kho thay đổi và quá 32 thẻ đều bị chặn. Hai lần SaveChanges nằm trong cùng giao dịch: nếu lưu thẻ/audit thất bại thì thay đổi kho bị rollback.

Không có migration/schema mới. Dùng equipment, equipment_rfid_tag, cabinet_health và audit_log hiện có. Chưa chạy thao tác cấp thiết bị trên DB thật trong kiểm thử.

## Camera

**Xem camera** hiển thị ngay trong popup, có nút chụp ảnh PNG ở độ phân giải nhận được. Backend chuyển JPEG VGA từ WebSocket ESP32 qua HTTP stream có bearer token; giữ kích thước khung, không thêm giới hạn FPS cố định. FPS thực tế phụ thuộc ESP32, chuyển JPEG và đường truyền.

Cấu hình backend cho từng room ID chính xác (không dùng tên hiển thị):

    ReturnVerification__Cameras__ROOM_ID__WebSocketUrl=wss://CAMERA_REACHABLE_HOST/ws

Nếu firmware cũ bật đăng nhập, thêm RequireAuthentication=true và AdminPassword dưới cùng nhánh camera. Firmware hiện tại không cần hai biến này.

Backend Render phải truy cập được địa chỉ camera này. IP LAN như 192.168.x.x chỉ dùng khi backend cùng mạng/VPN; Render cần tunnel/VPN hoặc gateway tới ESP32. Chưa cấu hình thì popup thông báo camera chưa kết nối với máy chủ. Không nhập URL camera tùy ý từ trình duyệt. Một phiên xem/chụp cho mỗi tủ; đóng xem camera trước khi xác minh trả bằng ảnh.

## Kiểm tra

- dotnet build tests/RfidEnrollment/RfidEnrollment.csproj -o .codex-temp/enrollment-tests -p:UseAppHost=false
- dotnet .codex-temp/enrollment-tests/RfidEnrollment.dll
- Frontend: npm run build, node tests/cameraFrames.mjs, node tests/cabinetPopup.cjs (Windows/Edge, API fixture, không chạm thiết bị/DB).
- Firmware: powershell -ExecutionPolicy Bypass -File tests/run-host.ps1; PlatformIO build, không flash.
