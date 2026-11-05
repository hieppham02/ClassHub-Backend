# Phân tích AI trong Giám sát IoT

Luồng: MQTT → kiểm tra source/node/sequence → iot_sensor → GET /api/admin/assets/nodes/{id}/analysis → trang Quản lý tài sản / Giám sát IoT. Giao diện cập nhật theo chu kỳ 30 giây hiện có.

Mô hình RandomForestClassifier (40 cây, độ sâu tối đa 6), huấn luyện 12.000 mẫu tổng hợp, kiểm tra trên 4.000 mẫu tổng hợp khác. Script tái tạo: tools/iot_model/train.py. Xuất cây vào Models/iot_forest.json; backend .NET suy luận trực tiếp, không cần Python trên Render. Nhãn huấn luyện được tạo từ các chế độ vận hành giả định, không phải sự cố đã xác minh. Các khoảng chế độ tổng hợp tách biệt nên độ chính xác trên tập này không phản ánh thực địa.

Đầu vào: nhiệt độ, độ ẩm, dòng điện và trạng thái sử dụng. Đầu ra: bình thường, tải điện sau sử dụng, nhiệt độ cao, hoặc cả hai. Điểm là tổng phiếu xác suất của forest, không phải xác suất hỏng thực tế.

Nguồn MOCK: chỉ lấy demo.context (IDLE, IN_USE, AFTER_USE), không lấy scenario hoặc danh sách thiết bị để suy luận. Lưu usageContext trong JSON readings hiện có, không migration. Phải nhận gói MQTT mới sau deploy; mẫu cũ thiếu context không được dùng kết luận quên tắt.

Nguồn REAL: ngữ cảnh từ phiên đã nhận đồ đang ACTIVE/IN_USE/RETURNING, hoặc phiên đã hoàn tất. Không có ngữ cảnh thì chỉ đánh giá nhiệt, không kết luận quên tắt; không suy ra có người trong phòng. Ngữ cảnh phiên trả chỉ là đại diện cần kiểm chứng.

Chỉ phân tích mẫu mới, clockSynced và đủ 3 chỉ số GOOD. Node bảo trì, dữ liệu cũ, thiếu chỉ số trả UNAVAILABLE, không hiển thị bình thường giả. Cảnh báo MOCK chỉ hiển thị phân tích, không tạo incident vận hành thật.

Không xác định chính xác đèn/TV/máy chiếu/điều hòa từ dòng điện tổng. Cần hiệu chuẩn, dữ liệu và đánh giá thực tế trước triển khai chính thức. Không thay thế mô hình dự báo pin đã có.

Kiểm tra: dotnet run --project tests/IotAnalysis/IotAnalysis.csproj; đối chiếu golden vectors với sklearn và các tải demo; test TestingNodes giữ phân tách REAL/MOCK. Build frontend và publish backend, kiểm tra model có trong output.

Demo: deploy BE + FE bản mới → tool đăng ký và chạy node → chọn phòng tương ứng trong Giám sát IoT → đổi occupied / forgot_tv / forgot_ac / high_temperature. Chờ gói mới và tối đa 30 giây cập nhật giao diện. Nhiệt độ tool tăng dần, chọn instant nếu cần demo ngay.


## Quy tắc nhóm vận hành

Chỉ phòng AFTER_USE (đã trả đồ) mới được tính cảnh báo. Phòng IN_USE (đang mượn, kể cả RETURNING chưa hoàn tất) thuộc nhóm Đang hoạt động và không sinh cảnh báo chỉ số trong mục này. Phiên thực đang mượn luôn ưu tiên ngữ cảnh bộ phát. Node offline/bảo trì không tính cảnh báo hiện tại.

Nguồn MOCK mặc định AFTER_USE khi thiếu ngữ cảnh hoặc gửi IDLE; ngữ cảnh IN_USE từ kịch bản occupied chuyển phòng sang nhóm hoạt động. Đây là mặc định hiển thị của bản demo, không tạo phiếu trả giả. Nguồn REAL không có phiên mượn/phiên trả được xác định là UNKNOWN, không tự coi là đã trả. Truy vấn tổng quan lấy danh sách phòng đang mượn và đã trả theo lô, không gọi API phân tích riêng cho mọi node.
