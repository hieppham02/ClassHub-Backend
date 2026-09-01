using ClassHub_API.Data;
using ClassHub_API.DTOs;
using ClassHub_API.Hubs;
using ClassHub_API.Models;
using ClassHub_API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using System.Globalization;
using System.Security.Claims;

namespace ClassHub_API.Controllers
{
    [Route("api/booking")]
    [ApiController]
    [Authorize]
    public class BookingController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly IMqttService _mqttService;
        private readonly IHubContext<CabinetHub> _hubContext;

        public BookingController(
            AppDbContext context,
            IMqttService mqttService,
            IHubContext<CabinetHub> hubContext)
        {
            _context = context;
            _mqttService = mqttService;
            _hubContext = hubContext;
        }

        // 1. Lấy danh sách phòng đã được đặt trong ngày và ca học
        [HttpGet("get-booked-rooms")]
        public async Task<IActionResult> GetBookedRooms([FromQuery] string date, [FromQuery] int slot)
        {
            DateTime parsedDate;
            try
            {
                parsedDate = DateTime.ParseExact(date, "dd-MM-yyyy", CultureInfo.InvariantCulture).Date;
            }
            catch
            {
                return BadRequest(new { message = "Sai định dạng ngày. Vui lòng dùng định dạng dd-MM-yyyy." });
            }

            var bookedRooms = await _context.phieu_muon
                .Where(p => p.ca_muon == slot
                         && p.ngay_muon.Date == parsedDate
                         && (p.trang_thai == "PENDING" || p.trang_thai == "ACTIVE" || p.trang_thai == "IN_USE"))
                .Select(p => p.ma_phong)
                .Distinct()
                .ToListAsync();

            return Ok(bookedRooms);
        }

        // 2. Đặt mượn phòng học & Thiết bị (Có ghi Audit Log kèm Vai trò)
        [HttpPost("dat-phong")]
        public async Task<IActionResult> DatPhong([FromBody] DatPhongDTO request)
        {
            var maSv = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(maSv)) return Unauthorized(new { message = "Không xác định được người dùng!" });

            DateTime parsedDate;
            try
            {
                parsedDate = DateTime.ParseExact(request.NgayMuon, "dd-MM-yyyy", CultureInfo.InvariantCulture).Date;
            }
            catch
            {
                return BadRequest(new { message = "Sai định dạng ngày mượn. Vui lòng dùng dd-MM-yyyy." });
            }

            // Kiểm tra sinh viên có đang nợ đơn mượn nào chưa hoàn tất không
            var dangCoPhieu = await _context.phieu_muon
                .AnyAsync(p => p.ma_sv == maSv && (p.trang_thai == "PENDING" || p.trang_thai == "ACTIVE" || p.trang_thai == "IN_USE"));

            if (dangCoPhieu)
                return BadRequest(new { message = "Bạn đang có 1 thiết bị chưa hoàn trả. Vui lòng trả thiết bị trước khi mượn mới!" });

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                // Kiểm tra chặn trùng lịch mượn phòng
                var daCoNguoiDat = await _context.phieu_muon
                    .AnyAsync(p => p.ma_phong == request.MaPhong
                                && p.ca_muon == request.CaMuon
                                && p.ngay_muon.Date == parsedDate
                                && (p.trang_thai == "PENDING" || p.trang_thai == "ACTIVE" || p.trang_thai == "IN_USE"));

                if (daCoNguoiDat)
                    return BadRequest(new { message = "Phòng này đã có người đăng ký trong ca học này!" });

                Random rnd = new Random();
                string newOtp = rnd.Next(100000, 999999).ToString();

                var phieuMoi = new PhieuMuon
                {
                    ma_sv = maSv,
                    ma_phong = request.MaPhong,
                    ngay_muon = parsedDate,
                    ca_muon = request.CaMuon,
                    trang_thai = "PENDING",
                    thoi_gian_tao = DateTime.Now,
                    otp = newOtp,
                    otp_expires_at = DateTime.Now.AddMinutes(30),
                    is_cabinet_open = false
                };

                _context.phieu_muon.Add(phieuMoi);

                // Lấy vai trò chính xác của người dùng
                var userRole = User.FindFirst(ClaimTypes.Role)?.Value;
                if (string.IsNullOrEmpty(userRole))
                {
                    var userInDb = await _context.tai_khoan.FirstOrDefaultAsync(u => u.ma_sv == maSv);
                    userRole = userInDb?.vai_tro ?? request.VaiTro ?? "SINHVIEN";
                }

                // GHI NHẬT KÝ AUDIT LOG: TAO_PHIEU_MUON (KÈM VAI TRÒ)
                var log = new NhatKyHeThong
                {
                    ma_sv = maSv,
                    hanh_dong = "TAO_PHIEU_MUON",
                    chi_tiet = $"{userRole} Đăng ký mượn phòng {phieuMoi.ma_phong} ",
                    thoi_gian = DateTime.Now,
                    ip_address = OtherHelper.GetClientIp(HttpContext),
                    user_agent = OtherHelper.GetClientOs(Request)
                };
                _context.nhat_ky_he_thong.Add(log);

                await _context.SaveChangesAsync();

                // GỬI MÃ OTP ĐẾN TOPIC PHÂN TẦNG: backend/cabinet/{ma_phong}/otp
                string topicOtp = $"backend/cabinet/{phieuMoi.ma_phong}/otp";
                var obj = new { id = phieuMoi.id, room = phieuMoi.ma_phong, otp = newOtp };
                string payload = JsonConvert.SerializeObject(obj);

                await _mqttService.PublishAsync(topicOtp, payload);
                await transaction.CommitAsync();

                // BẮN SIGNALR REALTIME CẬP NHẬT GIAO DIỆN
                await _hubContext.Clients.All.SendAsync("CabinetStatusChanged", new
                {
                    roomId = phieuMoi.ma_phong,
                    isOpen = false,
                    doorCondition = "Đóng",
                    isOnline = true,
                    lockStatus = "LOCKED",
                    status = "Đang mượn",
                    timestamp = DateTime.Now.ToString("HH:mm:ss")
                });

                return Ok(new { message = "Đăng ký mượn phòng thành công!" });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                return StatusCode(500, new { message = "Lỗi hệ thống: " + ex.Message });
            }
        }

        // 3. Hoàn tất trả thiết bị & Phòng học (Có ghi Audit Log kèm Vai trò)
        [HttpPut("return-room/{id}")]
        public async Task<IActionResult> ReturnRoom(int id, [FromQuery] string room)
        {
            var maSv = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(maSv)) return Unauthorized();

            var phieu = await _context.phieu_muon
                .FirstOrDefaultAsync(p => p.id == id && p.ma_phong == room && (p.ma_sv == maSv || p.ma_sv_uy_quyen == maSv));

            if (phieu == null) return NotFound(new { message = "Dữ liệu không khớp hoặc bạn không có quyền thao tác tủ này!" });

            if (phieu.trang_thai == "COMPLETED" || phieu.trang_thai == "CANCELED")
                return BadRequest(new { message = "Phòng này đã được trả hoặc bị hủy từ trước!" });

            phieu.trang_thai = "COMPLETED";
            phieu.thoi_gian_tra = DateTime.Now;
            phieu.ma_sv_tra = maSv;
            phieu.is_cabinet_open = false;

            var cab = await _context.thiet_bi_iot.FirstOrDefaultAsync(c => c.ma_phong == room);
            if (cab != null)
            {
                cab.trang_thai_khoa = "LOCKED";
                cab.lan_cuoi_online = DateTime.Now;
            }

            // Lấy vai trò chính xác của người trả
            var userRole = User.FindFirst(ClaimTypes.Role)?.Value;
            if (string.IsNullOrEmpty(userRole))
            {
                var userInDb = await _context.tai_khoan.FirstOrDefaultAsync(u => u.ma_sv == maSv);
                userRole = userInDb?.vai_tro ?? "SINHVIEN";
            }

            // GHI NHẬT KÝ AUDIT LOG: TRA_PHONG (KÈM VAI TRÒ)
            var log = new NhatKyHeThong
            {
                ma_sv = maSv,
                hanh_dong = "TRA_PHONG",
                chi_tiet = $"{userRole} Hoàn tất trả thiết bị phòng {room} ",
                thoi_gian = DateTime.Now,
                ip_address = OtherHelper.GetClientIp(HttpContext),
                user_agent = OtherHelper.GetClientOs(Request)
            };
            _context.nhat_ky_he_thong.Add(log);

            await _context.SaveChangesAsync();

            // GỬI LỆNH KHÓA TỦ MQTT ĐẾN ESP32
            string topicAction = $"backend/cabinet/{room}/action";
            var obj = new { id = phieu.id, room = phieu.ma_phong, action = "lock" };
            string payload = JsonConvert.SerializeObject(obj);

            await _mqttService.PublishAsync(topicAction, payload);

            // BẮN SIGNALR ĐỒNG BỘ TRẠNG THÁI REALTIME
            await _hubContext.Clients.All.SendAsync("CabinetStatusChanged", new
            {
                roomId = room,
                isOpen = false,
                doorCondition = "Đóng",
                isOnline = true,
                lockStatus = "LOCKED",
                status = "Trống",
                timestamp = DateTime.Now.ToString("HH:mm:ss")
            });

            return Ok(new { message = "Trả thiết bị thành công!" });
        }
    }
}