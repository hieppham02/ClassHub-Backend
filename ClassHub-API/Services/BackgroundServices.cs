using ClassHub_API.Data;
using ClassHub_API.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace ClassHub_API.Services
{
    public class CabinetStatusPayload
    {
        public string? id { get; set; }
        public string room { get; set; } = null!;
        public bool isOpen { get; set; }
        public bool? isOnline { get; set; } = true;
        public string? lockState { get; set; }
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

            // Đăng ký lắng nghe toàn bộ các tin nhắn gửi lên từ IoT
            await _mqttService.SubscribeAsync("iot/cabinet/+/status", HandleMqttStatusMessage);
            await _mqttService.SubscribeAsync("iot/cabinet/+/heartbeat", HandleMqttStatusMessage);
            await _mqttService.SubscribeAsync("tu_thiet_bi/STATUS", HandleMqttStatusMessage); // Backward compatibility

            Console.WriteLine(">> [BackgroundService] Da khoi chay MQTT Listener voi tien to 'iot/'");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CheckOfflineCabinetsAsync();
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[Watchdog Error] " + ex.Message);
                }

                await Task.Delay(5000, stoppingToken);
            }
        }

        private async void HandleMqttStatusMessage(string topic, string payload)
        {
            Console.WriteLine($"[MQTT Recv -> {topic}] {payload}");

            try
            {
                var data = JsonConvert.DeserializeObject<CabinetStatusPayload>(payload);
                if (data == null) return;

                // Tách lấy mã phòng từ topic (VD: iot/cabinet/DTD201/status -> DTD201)
                if (string.IsNullOrWhiteSpace(data.room) && topic.Contains("/"))
                {
                    var parts = topic.Split('/');
                    if (parts.Length >= 3) data.room = parts[2];
                }

                if (string.IsNullOrWhiteSpace(data.room)) return;

                using var scope = _scopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var cabinet = await dbContext.thiet_bi_iot.FirstOrDefaultAsync(c => c.ma_phong == data.room);
                if (cabinet != null)
                {
                    cabinet.lan_cuoi_online = DateTime.Now;
                    cabinet.trang_thai_mang = true;
                    cabinet.trang_thai_khoa = data.isOpen ? "UNLOCKED" : "LOCKED";
                }

                var activeBooking = await dbContext.phieu_muon
                    .Where(p => p.ma_phong == data.room && (p.trang_thai == "PENDING" || p.trang_thai == "ACTIVE" || p.trang_thai == "IN_USE"))
                    .OrderByDescending(p => p.thoi_gian_tao)
                    .FirstOrDefaultAsync();

                if (activeBooking != null)
                {
                    activeBooking.is_cabinet_open = data.isOpen;
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

                string displayStatus = (cabinet?.trang_thai_khoa == "ERROR") ? "Bảo trì" : (activeBooking != null ? "Đang mượn" : "Trống");
                string borrowerName = activeBooking?.MaSvNavigation?.ho_ten != null
                    ? $"{activeBooking.MaSvNavigation.ho_ten} ({activeBooking.ma_sv})"
                    : "---";

                await _hubContext.Clients.All.SendAsync("CabinetStatusChanged", new
                {
                    roomId = data.room,
                    isOpen = data.isOpen,
                    doorCondition = data.isOpen ? "Mở" : "Đóng",
                    isOnline = true,
                    status = displayStatus,
                    borrower = borrowerName,
                    lastOnline = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss")
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine("[MQTT Process Error] " + ex.Message);
            }
        }

        private async Task CheckOfflineCabinetsAsync()
        {
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var onlineCabinets = await dbContext.thiet_bi_iot
                .Where(c => c.trang_thai_mang == true)
                .ToListAsync();

            var now = DateTime.Now;
            bool hasChange = false;

            foreach (var cab in onlineCabinets)
            {
                double elapsedSeconds = cab.lan_cuoi_online.HasValue
                    ? (now - cab.lan_cuoi_online.Value).TotalSeconds
                    : 9999;

                if (elapsedSeconds > 30)
                {
                    cab.trang_thai_mang = false;
                    hasChange = true;

                    Console.WriteLine($">> [Watchdog Timeout] Tu {cab.ma_phong} da OFFLINE (Mat tin hieu {(int)elapsedSeconds}s)");

                    await _hubContext.Clients.All.SendAsync("CabinetStatusChanged", new
                    {
                        roomId = cab.ma_phong,
                        isOpen = cab.trang_thai_khoa == "UNLOCKED",
                        doorCondition = cab.trang_thai_khoa == "UNLOCKED" ? "Mở" : "Đóng",
                        isOnline = false,
                        lockStatus = cab.trang_thai_khoa,
                        status = "Bảo trì",
                        timestamp = DateTime.Now.ToString("HH:mm:ss")
                    });
                }
            }

            if (hasChange)
            {
                await dbContext.SaveChangesAsync();
            }
        }
    }
}