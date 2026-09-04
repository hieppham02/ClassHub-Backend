using ClassHub_API.Data;
using ClassHub_API.Hubs;
using ClassHub_API.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace ClassHub_API.Services
{
    public class CabinetStatusPayload
    {
        public string? id { get; set; }
        public string? room { get; set; }
        public bool? isOpen { get; set; }
        public bool? isOnline { get; set; }
        public string? lockState { get; set; }
    }

    public class BackgroundServices : BackgroundService
    {
        private static readonly string[] ActiveStatuses = { "PENDING", "ACTIVE", "IN_USE", "RETURNING" };

        private readonly IMqttService _mqttService;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IHubContext<CabinetHub> _hubContext;
        private readonly ILogger<BackgroundServices> _logger;
        private readonly SemaphoreSlim _messageLock = new(1, 1);

        public BackgroundServices(
            IMqttService mqttService,
            IServiceScopeFactory scopeFactory,
            IHubContext<CabinetHub> hubContext,
            ILogger<BackgroundServices> logger)
        {
            _mqttService = mqttService;
            _scopeFactory = scopeFactory;
            _hubContext = hubContext;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                await SubscribeToTopicsAsync(stoppingToken);

                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

                while (await timer.WaitForNextTickAsync(stoppingToken))
                {
                    try
                    {
                        await CheckOfflineCabinetsAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Lỗi khi kiểm tra trạng thái offline của tủ.");
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("BackgroundServices đã dừng.");
            }
        }

        private async Task SubscribeToTopicsAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await _mqttService.SubscribeAsync("iot/cabinet/+/status", HandleMqttStatusMessage);
                    await _mqttService.SubscribeAsync("iot/cabinet/+/heartbeat", HandleMqttStatusMessage);
                    await _mqttService.SubscribeAsync("tu_thiet_bi/STATUS", HandleMqttStatusMessage);

                    _logger.LogInformation("Đã đăng ký các MQTT topic trạng thái tủ.");
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Không thể đăng ký MQTT topic. Thử lại sau 5 giây.");
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
            }
        }

        private void HandleMqttStatusMessage(string topic, string payload)
        {
            _ = ProcessMqttStatusMessageAsync(topic, payload);
        }

        private async Task ProcessMqttStatusMessageAsync(string topic, string payload)
        {
            await _messageLock.WaitAsync();

            try
            {
                _logger.LogInformation("Nhận MQTT từ {Topic}: {Payload}", topic, payload);

                var data = JsonConvert.DeserializeObject<CabinetStatusPayload>(payload);
                if (data == null)
                {
                    _logger.LogWarning("Payload MQTT không hợp lệ từ topic {Topic}.", topic);
                    return;
                }

                string roomId = ResolveRoomId(topic, data.room);
                if (string.IsNullOrWhiteSpace(roomId))
                {
                    _logger.LogWarning("Không xác định được mã phòng từ topic {Topic}.", topic);
                    return;
                }

                using var scope = _scopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                DateTime now = DateTime.Now;
                bool incomingOnline = data.isOnline ?? true;
                string? incomingLockState = NormalizeLockState(data.lockState);
                bool hasLockState = data.isOpen.HasValue || incomingLockState != null;

                var cabinet = await dbContext.thiet_bi_iot.FirstOrDefaultAsync(c => c.ma_phong == roomId);

                if (cabinet != null)
                {
                    cabinet.trang_thai_mang = incomingOnline;

                    if (incomingOnline) cabinet.lan_cuoi_online = now;

                    if (hasLockState)
                    {
                        cabinet.trang_thai_khoa = incomingLockState ?? (data.isOpen == true ? "UNLOCKED" : "LOCKED");
                    }
                }
                else
                {
                    _logger.LogWarning("Không tìm thấy tủ IoT của phòng {RoomId} trong database.", roomId);
                }

                var borrowingSession = await dbContext.phieu_muon
                    .Include(p => p.MaSvNavigation)
                    .Where(p => p.ma_phong == roomId && ActiveStatuses.Contains(p.trang_thai))
                    .OrderByDescending(p => p.thoi_gian_tao)
                    .FirstOrDefaultAsync();

                bool isUnlocked = incomingLockState == "UNLOCKED"
                    || (incomingLockState == null && data.isOpen == true);

                bool isLocked = incomingLockState == "LOCKED"
                    || (incomingLockState == null && data.isOpen == false);

                if (borrowingSession != null && incomingOnline && hasLockState)
                {
                    borrowingSession.is_cabinet_open = isUnlocked;

                    if (isUnlocked && (borrowingSession.trang_thai == "PENDING" || borrowingSession.trang_thai == "IN_USE"))
                    {
                        borrowingSession.trang_thai = "ACTIVE";
                        borrowingSession.thoi_gian_nhan ??= now;
                    }

                    if (isLocked && borrowingSession.trang_thai == "RETURNING")
                    {
                        borrowingSession.trang_thai = "COMPLETED";
                        borrowingSession.thoi_gian_tra = now;
                        borrowingSession.is_cabinet_open = false;

                        dbContext.nhat_ky_he_thong.Add(new NhatKyHeThong
                        {
                            ma_sv = borrowingSession.ma_sv_tra ?? borrowingSession.ma_sv,
                            hanh_dong = LogAction.TRA_PHONG.ToString(),
                            chi_tiet = $"ESP32 xác nhận đã khóa tủ phòng {roomId}. Phiếu mượn đã hoàn tất.",
                            thoi_gian = now,
                            user_agent = "ClassHub IoT"
                        });

                        _logger.LogInformation(
                            "Phiếu {SessionId} đã chuyển từ RETURNING sang COMPLETED.",
                            borrowingSession.id);
                    }
                }

                await dbContext.SaveChangesAsync();

                bool stillActive = borrowingSession != null && ActiveStatuses.Contains(borrowingSession.trang_thai);
                string lockStatus = cabinet?.trang_thai_khoa
                    ?? incomingLockState
                    ?? (data.isOpen == true ? "UNLOCKED" : "LOCKED");

                string displayStatus;

                if (!incomingOnline || lockStatus == "ERROR")
                    displayStatus = "Bảo trì";
                else if (borrowingSession?.trang_thai == "RETURNING")
                    displayStatus = "Đang xác nhận trả";
                else if (stillActive)
                    displayStatus = "Đang mượn";
                else
                    displayStatus = "Trống";

                string borrowerName = stillActive && borrowingSession?.MaSvNavigation != null
                    ? $"{borrowingSession.MaSvNavigation.ho_ten} ({borrowingSession.ma_sv})"
                    : "---";

                await _hubContext.Clients.All.SendAsync("CabinetStatusChanged", new
                {
                    roomId,
                    isOpen = lockStatus == "UNLOCKED",
                    doorCondition = lockStatus == "UNLOCKED" ? "Mở" : "Đóng",
                    isOnline = cabinet?.trang_thai_mang ?? incomingOnline,
                    lockStatus,
                    status = displayStatus,
                    borrower = borrowerName,
                    lastOnline = cabinet?.lan_cuoi_online?.ToString("dd/MM/yyyy HH:mm:ss")
                });
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Payload MQTT không đúng định dạng JSON từ topic {Topic}.", topic);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi xử lý trạng thái MQTT từ topic {Topic}.", topic);
            }
            finally
            {
                _messageLock.Release();
            }
        }

        private async Task CheckOfflineCabinetsAsync()
        {
            await _messageLock.WaitAsync();

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                DateTime now = DateTime.Now;

                var timedOutCabinets = await dbContext.thiet_bi_iot
                    .Where(c => c.trang_thai_mang
                        && (!c.lan_cuoi_online.HasValue
                            || EF.Functions.DateDiffSecond(c.lan_cuoi_online.Value, now) > 30))
                    .ToListAsync();

                if (timedOutCabinets.Count == 0) return;

                foreach (var cabinet in timedOutCabinets)
                {
                    cabinet.trang_thai_mang = false;
                    _logger.LogWarning("Tủ {RoomId} đã offline do mất heartbeat.", cabinet.ma_phong);
                }

                await dbContext.SaveChangesAsync();

                foreach (var cabinet in timedOutCabinets)
                {
                    await _hubContext.Clients.All.SendAsync("CabinetStatusChanged", new
                    {
                        roomId = cabinet.ma_phong,
                        isOpen = cabinet.trang_thai_khoa == "UNLOCKED",
                        doorCondition = cabinet.trang_thai_khoa == "UNLOCKED" ? "Mở" : "Đóng",
                        isOnline = false,
                        lockStatus = cabinet.trang_thai_khoa,
                        status = "Bảo trì",
                        timestamp = now.ToString("HH:mm:ss")
                    });
                }
            }
            finally
            {
                _messageLock.Release();
            }
        }

        private static string ResolveRoomId(string topic, string? payloadRoom)
        {
            if (!string.IsNullOrWhiteSpace(payloadRoom)) return payloadRoom.Trim();

            string[] topicParts = topic.Split('/');
            return topicParts.Length >= 3 ? topicParts[2].Trim() : string.Empty;
        }

        private static string? NormalizeLockState(string? lockState)
        {
            if (string.IsNullOrWhiteSpace(lockState)) return null;

            string normalizedState = lockState.Trim().ToUpperInvariant();

            return normalizedState is "LOCKED" or "UNLOCKED" or "ERROR"
                ? normalizedState
                : null;
        }
    }
}