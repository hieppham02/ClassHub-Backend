using ClassHub_API.Data;
using ClassHub_API.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace ClassHub_API.Services
{
    public class CabinetStatusPayload
    {
        public string? id { get; set; }           // ID phiếu mượn (nếu có)
        public string room { get; set; } = null!; // Mã phòng (Bắt buộc, VD: "DTD201", "EAUT101")
        public bool isOpen { get; set; }          // Cửa đang Mở (true) hay Đóng (false)
        public bool? isOnline { get; set; } = true;
        public string? lockState { get; set; }    // "LOCKED" | "UNLOCKED"
    }

    public class BackgroundServices : BackgroundService
    {
        private readonly IMqttService _mqttService;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IHubContext<CabinetHub> _hubContext;

        public BackgroundServices(
            IMqttService mqttService,
            IServiceScopeFactory scopeFactory,
            IHubContext<CabinetHub> hubContext)
        {
            _mqttService = mqttService;
            _scopeFactory = scopeFactory;
            _hubContext = hubContext;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Delay(2000, stoppingToken);

            // Subscribe lắng nghe các topic trạng thái từ ESP32
            await _mqttService.SubscribeAsync("tu_thiet_bi/STATUS", HandleMqttStatusMessage);
            await _mqttService.SubscribeAsync("classhub/cabinet/+/event", HandleMqttStatusMessage);
            await _mqttService.SubscribeAsync("classhub/cabinet/+/heartbeat", HandleMqttStatusMessage);
            await _mqttService.SubscribeAsync("classhub/cabinet/+/status", HandleMqttStatusMessage);
        }

        private async void HandleMqttStatusMessage(string topic, string payload)
        {
            Console.WriteLine($"[MQTT Received] Topic: {topic} | Payload: {payload}");

            try
            {
                var data = JsonConvert.DeserializeObject<CabinetStatusPayload>(payload);
                if (data == null) return;

                // Nếu ESP32 gửi theo topic dạng classhub/cabinet/DTD201/event -> tách lấy room
                if (string.IsNullOrWhiteSpace(data.room) && topic.Contains("/"))
                {
                    var parts = topic.Split('/');
                    if (parts.Length >= 3) data.room = parts[2];
                }

                if (string.IsNullOrWhiteSpace(data.room)) return;

                using var scope = _scopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                // 1. CẬP NHẬT TRẠNG THÁI PHẦN CỨNG (thiet_bi_iot)
                var cabinet = await dbContext.thiet_bi_iot.FirstOrDefaultAsync(c => c.ma_phong == data.room);
                if (cabinet != null)
                {
                    cabinet.lan_cuoi_online = DateTime.Now;
                    cabinet.trang_thai_mang = data.isOnline ?? true;
                    cabinet.trang_thai_khoa = data.isOpen ? "UNLOCKED" : (data.lockState ?? "LOCKED");
                }

                // 2. CẬP NHẬT PHIẾU MƯỢN HIỆN HÀNH (phieu_muon)
                var activeBooking = await dbContext.phieu_muon
                    .Where(p => p.ma_phong == data.room && (p.trang_thai == "PENDING" || p.trang_thai == "ACTIVE" || p.trang_thai == "IN_USE"))
                    .OrderByDescending(p => p.thoi_gian_tao)
                    .FirstOrDefaultAsync();

                if (activeBooking != null)
                {
                    activeBooking.is_cabinet_open = data.isOpen;

                    // Nếu sinh viên đang ở trạng thái PENDING mà mở tủ lấy đồ -> Đổi sang ACTIVE
                    if ((activeBooking.trang_thai == "PENDING" || activeBooking.trang_thai == "IN_USE") && data.isOpen)
                    {
                        activeBooking.trang_thai = "ACTIVE";
                        if (!activeBooking.thoi_gian_nhan.HasValue)
                        {
                            activeBooking.thoi_gian_nhan = DateTime.Now;
                        }
                    }
                }

                await dbContext.SaveChangesAsync();
                Console.WriteLine($"[DB Synced] Phòng {data.room}: Cửa={(data.isOpen ? "Mở" : "Đóng")} | Mạng=Online");

                // 3. ĐẨY SIGNALR REALTIME XUỐNG FRONTEND VUE 3
                await _hubContext.Clients.All.SendAsync("CabinetStatusChanged", new
                {
                    roomId = data.room,
                    isOpen = data.isOpen,
                    doorCondition = data.isOpen ? "Mở" : "Đóng",
                    isOnline = data.isOnline ?? true,
                    lockStatus = data.isOpen ? "UNLOCKED" : "LOCKED",
                    timestamp = DateTime.Now.ToString("HH:mm:ss")
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine("Lỗi xử lý MQTT Background: " + ex.Message);
            }
        }
    }
}