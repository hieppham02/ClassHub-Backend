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
    [Authorize]
    public class AdminCabinetsController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly IMqttService _mqttService;
        private readonly IHubContext<CabinetHub> _hubContext;

        public AdminCabinetsController(
            AppDbContext context,
            IMqttService mqttService,
            IHubContext<CabinetHub> hubContext)
        {
            _context = context;
            _mqttService = mqttService;
            _hubContext = hubContext;
        }

        [HttpGet]
        public async Task<IActionResult> GetAllCabinets()
        {
            var cabinets = await _context.thiet_bi_iot
                .Include(c => c.MaPhongNavigation)
                .ThenInclude(p => p.MaToaNhaNavigation)
                .ToListAsync();

            var today = DateTime.Today;
            var tomorrow = today.AddDays(1);

            var activeBookings = await _context.phieu_muon
                .Where(p => (p.trang_thai == "ACTIVE" || p.trang_thai == "PENDING" || p.trang_thai == "IN_USE")
                         && p.ngay_muon >= today && p.ngay_muon < tomorrow)
                .Include(p => p.MaSvNavigation)
                .ToListAsync();

            var result = cabinets.Select(cab =>
            {
                var room = cab.MaPhongNavigation;
                var building = room?.MaToaNhaNavigation;
                var currentBooking = activeBookings.FirstOrDefault(b => b.ma_phong == cab.ma_phong);

                string status = "Trống";
                if (room?.trang_thai == "BAO_TRI" || room?.trang_thai == "TAM_KHOA" || cab.trang_thai_khoa == "ERROR")
                {
                    status = "Bảo trì";
                }
                else if (currentBooking != null)
                {
                    status = "Đang mượn";
                }

                bool isOpen = cab.trang_thai_khoa == "UNLOCKED" || (currentBooking?.is_cabinet_open == true);
                string doorCondition = isOpen ? "Mở" : "Đóng";

                return new
                {
                    id = cab.id,
                    roomId = cab.ma_phong,
                    name = cab.ten_tu,
                    roomName = room?.ten_phong ?? cab.ma_phong,
                    building = building?.ten_toa_nha ?? (cab.ma_phong.Substring(0, Math.Min(3, cab.ma_phong.Length))),
                    floor = $"Tầng {room?.tang ?? 1}",
                    macAddress = cab.mac_address,
                    mqttTopic = cab.mqtt_topic,
                    isOnline = cab.trang_thai_mang,
                    status = status,
                    doorCondition = doorCondition,
                    isDoorOpen = isOpen,
                    borrower = currentBooking?.MaSvNavigation?.ho_ten != null
                        ? $"{currentBooking.MaSvNavigation.ho_ten} ({currentBooking.ma_sv})"
                        : "---",
                    lastOnline = cab.lan_cuoi_online?.ToString("dd/MM/yyyy HH:mm") ?? "Chưa online"
                };
            }).ToList();

            return Ok(result);
        }

        [HttpPost("remote-open/{id}")]
        public async Task<IActionResult> RemoteOpenDoor(int id)
        {
            var adminId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "ADMIN";
            var cab = await _context.thiet_bi_iot.FindAsync(id);
            if (cab == null) return NotFound(new { message = "Không tìm thấy tủ đồ IoT này!" });

            cab.trang_thai_khoa = "UNLOCKED";
            await _context.SaveChangesAsync();

            // MQTT Publish
            string topic = cab.mqtt_topic ?? $"classhub/cabinet/{cab.ma_phong}";
            var obj = new { action = "open", admin_id = adminId, timestamp = DateTime.Now };
            string payload = JsonConvert.SerializeObject(obj);
            await _mqttService.PublishAsync(topic, payload);

            // Ghi log
            _context.nhat_ky_he_thong.Add(new NhatKyHeThong
            {
                ma_sv = adminId,
                hanh_dong = "ADMIN_MO_TU",
                chi_tiet = $"Admin mở khóa khẩn cấp tủ {cab.ten_tu} (Phòng {cab.ma_phong})",
                thoi_gian = DateTime.Now
            });
            await _context.SaveChangesAsync();

            // Bắn SignalR realtime
            await _hubContext.Clients.All.SendAsync("CabinetStatusChanged", new
            {
                roomId = cab.ma_phong,
                isOpen = true,
                doorCondition = "Mở",
                isOnline = cab.trang_thai_mang,
                lockStatus = "UNLOCKED",
                timestamp = DateTime.Now.ToString("HH:mm:ss")
            });

            return Ok(new { message = $"Đã gửi lệnh mở tủ {cab.ten_tu} thành công!" });
        }

        [HttpPost("remote-lock/{id}")]
        public async Task<IActionResult> RemoteLockDoor(int id)
        {
            var cab = await _context.thiet_bi_iot.FindAsync(id);
            if (cab == null) return NotFound(new { message = "Không tìm thấy tủ đồ IoT này!" });

            cab.trang_thai_khoa = "LOCKED";
            await _context.SaveChangesAsync();

            // Bắn SignalR realtime
            await _hubContext.Clients.All.SendAsync("CabinetStatusChanged", new
            {
                roomId = cab.ma_phong,
                isOpen = false,
                doorCondition = "Đóng",
                isOnline = cab.trang_thai_mang,
                lockStatus = "LOCKED",
                timestamp = DateTime.Now.ToString("HH:mm:ss")
            });

            return Ok(new { message = $"Đã khóa an toàn tủ {cab.ten_tu}!" });
        }
    }
}