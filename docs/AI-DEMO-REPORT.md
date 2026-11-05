# ClassHub — AI dự báo lịch thay pin và thống kê sử dụng phòng

## Kết quả đã triển khai

- Trang admin: /admin/ai, menu **AI & sử dụng phòng**.
- Mô hình Gradient Boosting Regression đã huấn luyện, xuất 180 cây quyết định và chạy suy luận trực tiếp trong ASP.NET Core.
- Giao diện nhập thông số, ba kịch bản demo, ngày dự kiến kiểm tra/thay pin, khoảng tham khảo và so sánh chu kỳ cố định.
- Dự báo theo hồ sơ thiết bị khi đã có ngày thay/sạc pin hoàn tất và lịch sử mượn.
- Thống kê phòng theo thời gian nhận/trả, tối đa 5 ca/phòng/ngày, phân biệt số lượt nhận với số ca sử dụng.
- Nút **Xuất báo cáo** tải Markdown từ trang web.

Mô hình là học máy đã được fit từ dữ liệu, không phải đổi tên các điều kiện if/else thành AI. Ngưỡng ưu tiên 7/21 ngày là quy tắc nghiệp vụ áp dụng sau kết quả hồi quy.

## Phạm vi dữ liệu và giới hạn

Kiểm tra chỉ đọc DB cục bộ ngày 06/11/2026: 2.267 phiên mượn, 2.183 phiên có khoảng nhận/trả hợp lệ, 178 phòng ROOM; chưa có hồ sơ pin, lịch thay pin hay kiểm tra pin. Đây là số bản ghi hệ thống, không phải xác nhận tất cả là đo thực tế.

Vì chưa có chu kỳ pin được ghi nhận, mô hình hiện tại huấn luyện trên **dữ liệu tổng hợp có nguồn SYNTHETIC**. Không chèn lịch sử giả vào DB. Độ chính xác dưới đây chỉ đánh giá trên tập tổng hợp, chưa thể suy rộng sang pin thực tế.

Giờ mượn là dữ liệu đại diện cho sử dụng, không phải thời gian thiết bị bật. Điều khiển và mic có kiểu tiêu hao khác nhau. Không dùng dự báo này để kết luận phần cứng sắp hỏng.

## Mô hình và cách huấn luyện

- Thuật toán: GradientBoostingRegressor, 180 cây, max_depth=3, learning_rate=0.06, loss=huber.
- 600 thiết bị ảo, mỗi thiết bị 2 chu kỳ; tổng 10,738 ảnh chụp đặc trưng theo thời gian.
- Tách theo thiết bị: 70% huấn luyện, 15% hiệu chỉnh khoảng sai số, 15% kiểm thử. Một thiết bị không xuất hiện ở nhiều tập.
- Số hàng tương ứng: 7,721 / 1,483 / 1,534.
- Nhãn: số ngày từ thời điểm quan sát đến hết chu kỳ pin.
- Mã mô hình: b027ce7b4f1a9a55acac.
- Hạt giống: 20261106; scikit-learn 1.9.0.

Đặc trưng: loại thiết bị, ngày từ lần thay pin, tổng giờ được mượn, số lượt nhận, giờ mượn trong 7 ngày gần nhất, số ngày có sử dụng trong 7 ngày, pin sạc hay không.

Bộ sinh dữ liệu dùng ngân sách tiêu hao theo thời gian và giờ được mượn, có khác biệt giữa thiết bị và biến động từng ngày. Các khoảng dung lượng trong script là giả định thử nghiệm, không phải thông số nhà sản xuất.

## Kết quả trên tập kiểm thử tổng hợp

| Chỉ tiêu | Giá trị |
|---|---:|
| MAE mô hình AI | 26.16 ngày |
| MAE lịch cố định theo loại thiết bị | 42.08 ngày |
| Giảm MAE so với baseline | 37.8% |
| RMSE | 39.73 ngày |
| R² | 0.823 |
| MAE riêng mic | 7.61 ngày |
| MAE riêng điều khiển | 39.34 ngày |
| Tỷ lệ nằm trong khoảng tham khảo trên tập thử | 92.2% |

Baseline lấy tuổi thọ trung bình chu kỳ của từng loại trong tập huấn luyện, rồi trừ tuổi pin hiện tại. Khoảng tham khảo dùng phân vị 90% sai số tuyệt đối trên tập hiệu chỉnh, riêng cho mic và điều khiển; không phải cam kết độ tin cậy trên thiết bị thật.

## Kịch bản demo 3–5 phút

1. Deploy backend và frontend mới nhất. Đăng nhập admin, mở /admin/ai.
2. Bấm **Mic ít dùng**: tuổi pin 7 ngày, tổng mượn 12 giờ, 16 lượt, tuần gần nhất 8 giờ / 5 ngày. Mô hình cho khoảng **13,0 ngày** còn lại.
3. Bấm **Mic dùng nhiều**: cùng tuổi pin 7 ngày nhưng tổng mượn 28 giờ, 35 lượt, tuần gần nhất 20 giờ / 7 ngày. Kết quả còn khoảng **6,2 ngày**, chuyển sang ưu tiên kiểm tra.
4. Chỉ ra: tuổi pin giống nhau nhưng mức sử dụng khác nhau làm dự báo khác nhau. Có thể nhập thông số rồi bấm **Chạy mô hình AI**.
5. Bấm **Điều khiển**: kịch bản 100 ngày tuổi pin, 80 giờ được mượn, mô hình cho khoảng **186,7 ngày**. Đây là kịch bản thử nghiệm, không phải tuổi thọ đảm bảo.
6. Chuyển tab **Sử dụng phòng**, bấm **Xem tháng trước**. Nếu cần nhiều dữ liệu cục bộ để minh họa, chọn **01/09/2026–30/09/2026**: DB hiện có 527 phiên có thời gian nhận/trả trong tháng này, gồm cả các loại vị trí; số dòng ROOM có thể thấp hơn.
7. Bấm tên một phòng để mở bảng ngày × 5 ca. Ba lần mượn trong cùng ca vẫn là ba lượt nhưng chỉ một ca sử dụng.
8. Bấm **Xuất báo cáo** để tải số liệu đang xem.

Trang dự báo theo hồ sơ để trống đúng thực trạng khi chưa đăng ký thiết bị. Để áp dụng dữ liệu thật: vào Pin micro, tạo hồ sơ từng mic/điều khiển, liên kết UID, ghi nhận lần thay/sạc pin thực tế. Không lấy ngày tạo hồ sơ làm ngày thay pin.

## Cách tính sử dụng phòng

- Lượt nhận: số phiên có thời điểm nhận hợp lệ bắt đầu trong khoảng được chọn.
- Ca sử dụng: ca có giao với khoảng nhận–trả; tối đa 5 ca/phòng/ngày.
- Giờ sử dụng: hợp các khoảng nhận–trả, chỉ tính phần giao với cửa sổ ca, không cộng trùng.
- Tỷ lệ ca = ca sử dụng / (số ngày × 5).
- Tỷ lệ thời gian = giờ sử dụng / tổng giờ ca khả dụng.
- Chuyển phòng tạo bản ghi riêng tại từng phòng; không ép số lượt mượn bằng số ca.
- Khả dụng là quy ước 5 ca mỗi ngày trong khoảng, chưa loại lịch nghỉ/lịch bảo trì theo ngày.
- Phòng không có bản ghi không đồng nghĩa phòng trống. Phát hiện dùng phòng nhưng không đăng ký chưa triển khai.

Mốc thời gian phiên mượn hiện được diễn giải theo múi giờ Việt Nam của dữ liệu cũ; lịch pin dùng UTC. Dữ liệu cũ lưu lẫn múi giờ cần được chuẩn hóa trước khi dùng để đánh giá thực tế.

## Kiến trúc và triển khai

Python/scikit-learn huấn luyện → JSON chứa cây, hệ số và metadata → .NET nạp một lần, suy luận → API admin → Vue.

Backend không cần cài Python/scikit-learn trên Render, không gọi dịch vụ AI trả phí. Mô hình khoảng 102 KB được đóng gói khi publish/Docker build. Không đổi schema, không tự ghi dự báo thử nghiệm vào bảng đánh giá REAL, không đổi firmware.

API:
- GET /api/admin/ai/model
- POST /api/admin/ai/battery/predict
- GET /api/admin/ai/battery/forecasts
- GET /api/admin/ai/rooms?from=2026-09-01&to=2026-09-30

## Kiểm tra đã thực hiện

- .NET suy luận khớp các vector xuất từ scikit-learn.
- Kiểm tra đầu vào sai, dữ liệu không hữu hạn, giới hạn tuổi pin và giờ sử dụng.
- Kiểm tra hợp khoảng thời gian, đếm một ca khi nhiều lượt trùng nhau, tối đa 5 ca, ca qua đêm.
- HTTP localhost: 401 khi chưa đăng nhập, 403 khi không phải admin, 200 khi dự báo hợp lệ, 400 khi đầu vào sai.
- Chạy SELECT trên DB hiện tại, không INSERT/UPDATE/DDL. 33 kiểm tra backend/API qua.
- Trình duyệt: đổi kịch bản làm đổi dự báo; tải Markdown; thống kê, bảng ca; bố cục desktop/mobile. API fixture dùng chính mô hình xuất để tính kết quả.
- Chưa đánh giá trên chu kỳ pin thật; chưa xác minh thiết bị vật lý hay kết quả cloud sau deploy.

Lệnh kiểm tra:
    dotnet build tests/AiInsights/AiInsights.csproj -o .codex-temp/ai-tests -p:UseAppHost=false
    dotnet .codex-temp/ai-tests/AiInsights.dll

Huấn luyện lại:
    pip install -r tools/battery_model/requirements.txt
    python tools/battery_model/train.py

Khi có dữ liệu thật, cần tạo mẫu chỉ từ thông tin biết tại thời điểm dự báo, nhãn từ lần hết/thay pin được xác nhận; tách thiết bị/thời gian trước đánh giá. Không đưa dữ liệu tổng hợp vào tập kiểm thử thực tế.

## Tài liệu thuật toán

- Gradient Boosting Regression: https://scikit-learn.org/stable/modules/generated/sklearn.ensemble.GradientBoostingRegressor.html
- Chia tập theo nhóm: https://scikit-learn.org/stable/modules/generated/sklearn.model_selection.GroupShuffleSplit.html
