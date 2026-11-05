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
        public bool? doorOpen { get; set; }
        public CabinetHealthPayload? health { get; set; }
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
        private readonly SemaphoreSlim _qrMessageLock = new(1, 1);

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
                    await _mqttService.SubscribeAsync("iot/cabinet/+/qr-result", HandleQrResult);
                    await _mqttService.SubscribeAsync("iot/cabinet/+/status", HandleMqttStatusMessage);
                    await _mqttService.SubscribeAsync("iot/cabinet/+/heartbeat", HandleMqttStatusMessage);
                    await _mqttService.SubscribeAsync("tu_thiet_bi/STATUS", HandleMqttStatusMessage);
                    await _mqttService.SubscribeAsync("iot/node/+/telemetry", (topic,body) => { _ = ProcessNodeTelemetry(topic,body); });

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

        private readonly SemaphoreSlim _nodeLock = new(1,1);
        public async Task ProcessNodeTelemetry(string topic,string body) {
            await _nodeLock.WaitAsync();
            try { using var scope=_scopeFactory.CreateScope();
                await NodeTelemetry.Accept(scope.ServiceProvider.GetRequiredService<AppDbContext>(),topic,body,DateTime.UtcNow);
            } catch(Exception ex) { _logger.LogError(ex,"Node telemetry failed for {Topic}",topic); }
            finally { _nodeLock.Release(); }
        }
        private void HandleQrResult(string topic, string payload) { _ = ProcessQrResult(topic, payload); }
        public async Task ProcessQrResult(string topic, string payload)
        {
            await _qrMessageLock.WaitAsync();
            try {
                if (payload.Length > 8192) return;
                var parts = topic.Split('/');
                if (parts.Length != 4 || parts[0] != "iot" || parts[1] != "cabinet" || parts[3] != "qr-result") return;
                var result = System.Text.Json.JsonSerializer.Deserialize<QrDeviceResult>(payload, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (result == null || string.IsNullOrWhiteSpace(result.CommandId)) return;
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var room = await CabinetAddress.RoomForDevice(db, parts[2]);
                if (room == null) return;
                await scope.ServiceProvider.GetRequiredService<QrAccessService>().DeviceResult(room, result);
            } catch (Exception error) { _logger.LogError(error, "Lỗi xử lý phản hồi QR từ {Topic}", topic); }
            finally { _qrMessageLock.Release(); }
        }

        private void HandleMqttStatusMessage(string topic, string payload)
        {
            _ = ProcessMqttStatusMessageAsync(topic, payload);
        }

        public async Task ProcessMqttStatusMessageAsync(string topic, string payload)
        {
            await _messageLock.WaitAsync();

            try
            {
                if (payload.Length > 8192) return;
                _logger.LogInformation("Nhận MQTT từ {Topic}: {Bytes} characters", topic, payload.Length);

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

                // An MQTT topic identifies the device. A conflicting payload may not select another room.
                if (topic.StartsWith("iot/cabinet/", StringComparison.Ordinal)) {
                    var levels=topic.Split('/');
                    if (levels.Length!=4 || !string.IsNullOrWhiteSpace(data.room) && data.room!=levels[2]) return;
                    var mapped=await CabinetAddress.RoomForDevice(dbContext, levels[2]);
                    if (mapped==null) return;
                    roomId=mapped;
                }

                DateTime now = DateTime.Now;
                bool incomingOnline = data.isOnline ?? true;
                string? incomingLockState = NormalizeLockState(data.lockState);
                bool hasLockState = data.isOpen.HasValue || incomingLockState != null;

                var cabinet = await dbContext.thiet_bi_iot.Include(c => c.MaPhongNavigation).FirstOrDefaultAsync(c => c.ma_phong == roomId);

                if (cabinet != null)
                {
                    if (data.health != null && !await CabinetHealthService.Accept(dbContext, roomId, data.health, DateTime.UtcNow)) return;
                    cabinet.trang_thai_mang = incomingOnline;

                    if (incomingOnline) cabinet.lan_cuoi_online = now;

                    if (hasLockState && cabinet.trang_thai_khoa != "ERROR")
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

                // Borrow/return transitions require correlated QR device events.
                // Generic status and heartbeat only update cabinet connectivity.
                await dbContext.SaveChangesAsync();

                bool stillActive = borrowingSession != null && ActiveStatuses.Contains(borrowingSession.trang_thai);
                string lockStatus = cabinet?.trang_thai_khoa
                    ?? incomingLockState
                    ?? (data.isOpen == true ? "UNLOCKED" : "LOCKED");

                string displayStatus;

                if (await AllocationPolicy.IsBlocked(dbContext, roomId) || lockStatus == "ERROR" || cabinet?.MaPhongNavigation?.trang_thai is "BAO_TRI" or "TAM_KHOA")
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

                var healthRow = await dbContext.CabinetHealths.FindAsync(roomId);
                var health = CabinetHealthService.View(healthRow, DateTime.UtcNow);
                var rememberedDoors = await CabinetDisplay.LastDoors(dbContext, [roomId]);
                var measuredDoor = CabinetDisplay.DoorOpen(data.doorOpen ?? health?.Metrics.DoorOpen,
                    rememberedDoors.TryGetValue(roomId,out var rememberedDoor)?rememberedDoor:null);
                await _hubContext.Clients.All.SendAsync("CabinetStatusChanged", new
                {
                    roomId, health,
                    isOpen = measuredDoor,
                    isDoorOpen = measuredDoor,
                    doorCondition = CabinetDisplay.DoorLabel(measuredDoor),
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
                    .Include(c=>c.MaPhongNavigation)
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

                var rooms = timedOutCabinets.Select(c=>c.ma_phong).OfType<string>().ToArray();
                var rememberedDoors = await CabinetDisplay.LastDoors(dbContext, rooms);
                var healthRows = rooms.Length==0 ? new Dictionary<string,CabinetHealth>() :
                    await dbContext.CabinetHealths.AsNoTracking().Where(h=>rooms.Contains(h.RoomId)).ToDictionaryAsync(h=>h.RoomId);
                var bookings = rooms.Length==0 ? new List<PhieuMuon>() :
                    await dbContext.phieu_muon.AsNoTracking().Where(s=>rooms.Contains(s.ma_phong) && ActiveStatuses.Contains(s.trang_thai))
                    .OrderByDescending(s=>s.thoi_gian_tao).ToListAsync();
                var blockedRooms = await AllocationPolicy.BlockedRooms(dbContext);
                foreach (var cabinet in timedOutCabinets)
                {
                    var room = cabinet.ma_phong ?? "";
                    healthRows.TryGetValue(room,out var healthRow);
                    var health = CabinetHealthService.View(healthRow,DateTime.UtcNow);
                    bool doorOpen = CabinetDisplay.DoorOpen(health?.Metrics.DoorOpen,
                        rememberedDoors.TryGetValue(room,out var rememberedDoor)?rememberedDoor:null);
                    var booking = bookings.FirstOrDefault(s=>s.ma_phong==room);
                    await _hubContext.Clients.All.SendAsync("CabinetStatusChanged", new
                    {
                        roomId = cabinet.ma_phong,
                        isOpen = doorOpen,
                        isDoorOpen = doorOpen,
                        doorCondition = CabinetDisplay.DoorLabel(doorOpen),
                        isOnline = false,
                        lockStatus = cabinet.trang_thai_khoa,
                        status = CabinetDisplay.Status(cabinet,cabinet.MaPhongNavigation?.trang_thai,booking,blockedRooms.Contains(room)),
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