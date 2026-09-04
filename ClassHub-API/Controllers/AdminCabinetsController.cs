using ClassHub_API.Data;
using ClassHub_API.Hubs;
using ClassHub_API.Models;
using ClassHub_API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using System.Security.Claims;

namespace ClassHub_API.Controllers
{
    [Route("api/admin/cabinets")]
    [ApiController]
    [Authorize(Roles = "ADMIN")]
    public class AdminCabinetsController : ControllerBase
    {
        private static readonly string[] ActiveStatuses = { "PENDING", "ACTIVE", "IN_USE", "RETURNING" };

        private readonly AppDbContext _context;
        private readonly IMqttService _mqttService;
        private readonly IHubContext<CabinetHub> _hubContext;
        private readonly ILogger<AdminCabinetsController> _logger;

        public AdminCabinetsController(
            AppDbContext context,
            IMqttService mqttService,
            IHubContext<CabinetHub> hubContext,
            ILogger<AdminCabinetsController> logger)
        {
            _context = context;
            _mqttService = mqttService;
            _hubContext = hubContext;
            _logger = logger;
        }

        [HttpGet]
        public async Task<IActionResult> GetAllCabinets()
        {
            var cabinets = await _context.thiet_bi_iot
                .AsNoTracking()
                .Include(c => c.MaPhongNavigation)
                .ThenInclude(p => p.MaToaNhaNavigation)
                .OrderBy(c => c.ma_phong)
                .ToListAsync();

            var activeBookings = await _context.phieu_muon
                .AsNoTracking()
                .Include(p => p.MaSvNavigation)
                .Where(p => ActiveStatuses.Contains(p.trang_thai))
                .OrderByDescending(p => p.thoi_gian_tao)
                .ToListAsync();

            var currentBookings = activeBookings
                .GroupBy(p => p.ma_phong)
                .ToDictionary(group => group.Key, group => group.First());

            var result = cabinets.Select(cabinet =>
            {
                string roomId = cabinet.ma_phong ?? "---";
                var room = cabinet.MaPhongNavigation;
                var building = room?.MaToaNhaNavigation;

                currentBookings.TryGetValue(roomId, out PhieuMuon? currentBooking);

                bool isUnlocked = cabinet.trang_thai_khoa == "UNLOCKED";
                string status = GetCabinetDisplayStatus(cabinet, room?.trang_thai, currentBooking);

                return new
                {
                    id = cabinet.id,
                    roomId,
                    name = cabinet.ten_tu,
                    roomName = room?.ten_phong ?? roomId,
                    building = building?.ten_toa_nha ?? GetBuildingCode(roomId),
                    floor = $"Tầng {room?.tang ?? 1}",
                    macAddress = cabinet.mac_address,
                    mqttTopic = cabinet.mqtt_topic,
                    isOnline = cabinet.trang_thai_mang,
                    status,
                    doorCondition = isUnlocked ? "Mở" : "Đóng",
                    isDoorOpen = isUnlocked,
                    lockStatus = cabinet.trang_thai_khoa,
                    borrower = currentBooking?.MaSvNavigation != null
                        ? $"{currentBooking.MaSvNavigation.ho_ten} ({currentBooking.ma_sv})"
                        : "---",
                    borrowingStatus = currentBooking?.trang_thai,
                    lastOnline = cabinet.lan_cuoi_online?.ToString("dd/MM/yyyy HH:mm:ss") ?? "Chưa online"
                };
            });

            return Ok(result);
        }

        [HttpPost("remote-open/{id:int}")]
        public async Task<IActionResult> RemoteOpenDoor(int id)
        {
            string? adminId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrWhiteSpace(adminId))
                return Unauthorized(new { message = "Không xác định được quản trị viên." });

            var cabinet = await _context.thiet_bi_iot
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.id == id);

            if (cabinet == null) return NotFound(new { message = "Không tìm thấy tủ IoT." });
            if (string.IsNullOrWhiteSpace(cabinet.ma_phong))
                return BadRequest(new { message = "Tủ chưa được gán vào phòng học." });

            if (!cabinet.trang_thai_mang)
                return BadRequest(new { message = "Tủ đang offline. Không thể gửi lệnh mở." });

            if (cabinet.trang_thai_khoa == "ERROR")
                return BadRequest(new { message = "Tủ đang gặp lỗi hoặc bảo trì." });

            var activeBooking = await _context.phieu_muon
                .AsNoTracking()
                .Where(p => p.ma_phong == cabinet.ma_phong && ActiveStatuses.Contains(p.trang_thai))
                .OrderByDescending(p => p.thoi_gian_tao)
                .FirstOrDefaultAsync();

            AddAuditLog(
                adminId,
                "ADMIN_YEU_CAU_MO_TU",
                $"Quản trị viên yêu cầu mở tủ {cabinet.ten_tu}, phòng {cabinet.ma_phong}.");

            try
            {
                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Không thể ghi audit log khi admin mở tủ {CabinetId}.", cabinet.id);
                return StatusCode(StatusCodes.Status500InternalServerError, new { message = "Không thể ghi nhận yêu cầu mở tủ." });
            }

            string topic = $"backend/cabinet/{cabinet.ma_phong}/action";
            string payload = JsonConvert.SerializeObject(new
            {
                id = activeBooking?.id.ToString() ?? "0",
                room = cabinet.ma_phong,
                action = "open",
                ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });

            try
            {
                await _mqttService.PublishAsync(topic, payload);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Không gửi được lệnh mở tới tủ {CabinetId}.", cabinet.id);

                return StatusCode(StatusCodes.Status503ServiceUnavailable, new
                {
                    message = "Không gửi được lệnh mở tới tủ. Hãy thử lại."
                });
            }

            return Accepted(new
            {
                message = $"Đã gửi lệnh mở tủ {cabinet.ten_tu}. Đang chờ ESP32 xác nhận."
            });
        }

        [HttpPost("remote-lock/{id:int}")]
        public async Task<IActionResult> RemoteLockDoor(int id)
        {
            string? adminId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrWhiteSpace(adminId))
                return Unauthorized(new { message = "Không xác định được quản trị viên." });

            var cabinet = await _context.thiet_bi_iot
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.id == id);

            if (cabinet == null) return NotFound(new { message = "Không tìm thấy tủ IoT." });
            if (string.IsNullOrWhiteSpace(cabinet.ma_phong))
                return BadRequest(new { message = "Tủ chưa được gán vào phòng học." });

            if (!cabinet.trang_thai_mang)
                return BadRequest(new { message = "Tủ đang offline. Không thể gửi lệnh khóa." });

            if (cabinet.trang_thai_khoa == "ERROR")
                return BadRequest(new { message = "Tủ đang gặp lỗi hoặc bảo trì." });

            var activeBooking = await _context.phieu_muon
                .AsNoTracking()
                .Where(p => p.ma_phong == cabinet.ma_phong && ActiveStatuses.Contains(p.trang_thai))
                .OrderByDescending(p => p.thoi_gian_tao)
                .FirstOrDefaultAsync();

            AddAuditLog(
                adminId,
                "ADMIN_YEU_CAU_KHOA_TU",
                $"Quản trị viên yêu cầu khóa tủ {cabinet.ten_tu}, phòng {cabinet.ma_phong}.");

            try
            {
                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Không thể ghi audit log khi admin khóa tủ {CabinetId}.", cabinet.id);
                return StatusCode(StatusCodes.Status500InternalServerError, new { message = "Không thể ghi nhận yêu cầu khóa tủ." });
            }

            string topic = $"backend/cabinet/{cabinet.ma_phong}/action";
            string payload = JsonConvert.SerializeObject(new
            {
                id = activeBooking?.id.ToString() ?? "0",
                room = cabinet.ma_phong,
                action = "lock",
                ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });

            try
            {
                await _mqttService.PublishAsync(topic, payload);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Không gửi được lệnh khóa tới tủ {CabinetId}.", cabinet.id);

                return StatusCode(StatusCodes.Status503ServiceUnavailable, new
                {
                    message = "Không gửi được lệnh khóa tới tủ. Hãy thử lại."
                });
            }

            return Accepted(new
            {
                message = $"Đã gửi lệnh khóa tủ {cabinet.ten_tu}. Đang chờ ESP32 xác nhận."
            });
        }

        [HttpPatch("{id:int}/toggle-maintenance")]
        public async Task<IActionResult> ToggleMaintenance(int id)
        {
            string? adminId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrWhiteSpace(adminId))
                return Unauthorized(new { message = "Không xác định được quản trị viên." });

            var cabinet = await _context.thiet_bi_iot
                .Include(c => c.MaPhongNavigation)
                .FirstOrDefaultAsync(c => c.id == id);

            if (cabinet == null) return NotFound(new { message = "Không tìm thấy tủ IoT." });

            var room = cabinet.MaPhongNavigation;
            if (room == null) return BadRequest(new { message = "Không tìm thấy phòng học của tủ." });

            bool enableMaintenance = room.trang_thai != "BAO_TRI";
            room.trang_thai = enableMaintenance ? "BAO_TRI" : "HOAT_DONG";

            AddAuditLog(
                adminId,
                enableMaintenance ? "BAT_BAO_TRI_TU" : "TAT_BAO_TRI_TU",
                $"{(enableMaintenance ? "Bật" : "Tắt")} chế độ bảo trì tủ {cabinet.ten_tu}, phòng {cabinet.ma_phong}.");

            try
            {
                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Không thể đổi trạng thái bảo trì của tủ {CabinetId}.", cabinet.id);

                return StatusCode(
                    StatusCodes.Status500InternalServerError,
                    new { message = "Không thể đổi trạng thái bảo trì." });
            }

            string displayStatus = enableMaintenance ? "Bảo trì" : "Trống";

            try
            {
                await _hubContext.Clients.All.SendAsync("CabinetStatusChanged", new
                {
                    roomId = cabinet.ma_phong,
                    status = displayStatus,
                    isOnline = cabinet.trang_thai_mang,
                    isOpen = cabinet.trang_thai_khoa == "UNLOCKED",
                    doorCondition = cabinet.trang_thai_khoa == "UNLOCKED" ? "Mở" : "Đóng",
                    lockStatus = cabinet.trang_thai_khoa,
                    timestamp = DateTime.Now.ToString("HH:mm:ss")
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Không gửi được SignalR khi đổi bảo trì tủ {CabinetId}.", cabinet.id);
            }

            string message = enableMaintenance
                ? "Đã chuyển tủ sang chế độ bảo trì."
                : "Đã chuyển tủ sang hoạt động bình thường.";

            return Ok(new { message, status = displayStatus });
        }

        private void AddAuditLog(string adminId, string action, string detail)
        {
            _context.nhat_ky_he_thong.Add(new NhatKyHeThong
            {
                ma_sv = adminId,
                hanh_dong = action,
                chi_tiet = detail,
                thoi_gian = DateTime.Now,
                ip_address = Helper.GetClientIp(HttpContext),
                user_agent = Helper.GetClientOs(Request)
            });
        }

        private static string GetCabinetDisplayStatus(ThietBiIot cabinet, string? roomStatus, PhieuMuon? booking)
        {
            if (roomStatus is "BAO_TRI" or "TAM_KHOA" || cabinet.trang_thai_khoa == "ERROR") return "Bảo trì";
            if (!cabinet.trang_thai_mang) return "Mất kết nối";
            if (booking?.trang_thai == "RETURNING") return "Đang xác nhận trả";
            if (booking != null) return "Đang mượn";

            return "Trống";
        }

        private static string GetBuildingCode(string roomId)
        {
            if (string.IsNullOrWhiteSpace(roomId) || roomId == "---") return "---";
            return roomId[..Math.Min(3, roomId.Length)];
        }
    }
}