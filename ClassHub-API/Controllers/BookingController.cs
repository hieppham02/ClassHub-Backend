using ClassHub_API.Data;
using ClassHub_API.DTOs;
using ClassHub_API.Models;
using ClassHub_API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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

        public BookingController(AppDbContext context, IMqttService mqttService)
        {
            _context = context;
            _mqttService = mqttService;
        }

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

            var dangCoPhieu = await _context.phieu_muon
                .AnyAsync(p => p.ma_sv == maSv && (p.trang_thai == "PENDING" || p.trang_thai == "ACTIVE" || p.trang_thai == "IN_USE"));

            if (dangCoPhieu)
                return BadRequest(new { message = "Bạn đang có 1 thiết bị chưa hoàn trả. Vui lòng trả thiết bị trước khi mượn mới!" });

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                // KIỂM TRA CHẶN TRÙNG LẶP (Chặn 2 User mượn cùng 1 phòng)
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
                    otp_expires_at = DateTime.Now.AddMinutes(30), // Thời hạn OTP 30 phút
                    is_cabinet_open = false
                };

                _context.phieu_muon.Add(phieuMoi);
                await _context.SaveChangesAsync();

                string topic = "tu_thiet_bi/OTP";
                var obj = new { id = phieuMoi.id, room = phieuMoi.ma_phong, otp = newOtp };
                string payload = JsonConvert.SerializeObject(obj);

                await _mqttService.PublishAsync(topic, payload);
                await transaction.CommitAsync();

                return Ok(new { message = "Đăng ký mượn phòng thành công!" });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                return StatusCode(500, new { message = "Lỗi hệ thống: " + ex.Message });
            }
        }

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

            await _context.SaveChangesAsync();

            return Ok(new { message = "Trả thiết bị thành công!" });
        }
    }
}