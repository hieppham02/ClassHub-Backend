using ClassHub_API.Data;
using ClassHub_API.DTOs;
using ClassHub_API.Hubs;
using ClassHub_API.Models;
using ClassHub_API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Data;
using System.Globalization;
using System.Security.Claims;

namespace ClassHub_API.Controllers
{
    [Route("api/booking")]
    [ApiController]
    [Authorize]
    public class BookingController : ControllerBase
    {
        private static readonly string[] ActiveStatuses = { "PENDING", "ACTIVE", "IN_USE", "RETURNING" };

        private readonly QrAccessService _qrFlow;
        private readonly AppDbContext _context;
        private readonly IHubContext<CabinetHub> _hubContext;
        private readonly ILogger<BookingController> _logger;

        public BookingController(AppDbContext context, IHubContext<CabinetHub> hubContext, ILogger<BookingController> logger, QrAccessService qrFlow)
        {
            _qrFlow = qrFlow;
            _context = context;
            _hubContext = hubContext;
            _logger = logger;
        }

        [HttpGet("get-booked-rooms")]
        public async Task<IActionResult> GetBookedRooms([FromQuery] string date, [FromQuery] int slot)
        {
            bool validDate = DateTime.TryParseExact(
                date,
                "dd-MM-yyyy",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTime parsedDate);

            if (!validDate)
                return BadRequest(new { message = "Sai định dạng ngày. Vui lòng dùng định dạng dd-MM-yyyy." });

            if (slot <= 0) return BadRequest(new { message = "Ca học không hợp lệ." });

            DateTime startDate = parsedDate.Date;
            DateTime endDate = startDate.AddDays(1);

            var bookedRooms = await _context.phieu_muon
                .AsNoTracking()
                .Where(p => p.ca_muon == slot
                    && p.ngay_muon >= startDate
                    && p.ngay_muon < endDate
                    && ActiveStatuses.Contains(p.trang_thai))
                .Select(p => p.ma_phong)
                .Distinct()
                .ToListAsync();

            return Ok(bookedRooms);
        }

        [HttpPost("dat-phong")]
        public async Task<IActionResult> DatPhong([FromBody] DatPhongDTO request)
        {
            string? currentUserId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrWhiteSpace(currentUserId))
                return Unauthorized(new { message = "Không xác định được người dùng." });

            if (request == null || string.IsNullOrWhiteSpace(request.MaPhong))
                return BadRequest(new { message = "Mã phòng không được để trống." });

            bool validDate = DateTime.TryParseExact(
                request.NgayMuon,
                "dd-MM-yyyy",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTime borrowingDate);

            if (!validDate)
                return BadRequest(new { message = "Sai định dạng ngày mượn. Vui lòng dùng định dạng dd-MM-yyyy." });

            borrowingDate = borrowingDate.Date;
            if (borrowingDate < DateTime.Today)
                return BadRequest(new { message = "Không thể đăng ký mượn cho ngày đã qua." });

            if (request.CaMuon <= 0) return BadRequest(new { message = "Ca học không hợp lệ." });

            string requestedRoomId = request.MaPhong.Trim();

            var userAccount = await _context.tai_khoan
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.ma_sv == currentUserId);

            if (userAccount == null) return Unauthorized(new { message = "Tài khoản không tồn tại." });
            if (!userAccount.trang_thai) return StatusCode(StatusCodes.Status403Forbidden, new { message = "Tài khoản đã bị khóa." });

            var room = await _context.phong_hoc
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.ma_phong == requestedRoomId);

            if (room == null) return NotFound(new { message = "Không tìm thấy phòng học." });
            if (room.trang_thai != "HOAT_DONG")
                return BadRequest(new { message = "Phòng học hiện không hoạt động." });

            bool validSlot = await _context.cau_hinh_ca_hoc
                .AsNoTracking()
                .AnyAsync(c => c.ca_id == request.CaMuon);

            if (!validSlot) return BadRequest(new { message = "Ca học không tồn tại." });

            var cabinet = await _context.thiet_bi_iot
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.ma_phong == room.ma_phong);

            if (cabinet == null) return BadRequest(new { message = "Phòng này chưa được cấu hình tủ IoT." });
            if (!cabinet.trang_thai_mang) return BadRequest(new { message = "Tủ đang offline. Không thể tạo phiên mượn." });
            if (await AllocationPolicy.IsBlocked(_context, room.ma_phong)) return BadRequest(new { message = "Tủ đang bị chặn cấp phát do sự cố chưa được gỡ chặn." });
            if (cabinet.trang_thai_khoa == "ERROR") return BadRequest(new { message = "Tủ đang gặp lỗi hoặc bảo trì." });

            if (await _context.thiet_bi.AnyAsync(e => e.ma_phong == requestedRoomId && e.UsageScope == "CABINET"
                && (e.tinh_trang != "TOT" || e.so_luong <= 0)))
                return BadRequest(new { message = "Bộ thiết bị của phòng có thiết bị hỏng hoặc cần bảo trì. Vui lòng liên hệ quản trị viên." });

            DateTime now = DateTime.Now;

            var newBorrowingSession = new PhieuMuon
            {
                ma_sv = currentUserId,
                ma_phong = room.ma_phong,
                ngay_muon = borrowingDate,
                ca_muon = request.CaMuon,
                trang_thai = "PENDING",
                thoi_gian_tao = now,
                QrEnabled = true,
                is_cabinet_open = false
            };

            try
            {
                await using var transaction = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable);

                bool hasActiveSession = await _context.phieu_muon
                    .AnyAsync(p => p.ma_sv == currentUserId && ActiveStatuses.Contains(p.trang_thai));

                if (hasActiveSession)
                    return BadRequest(new { message = "Bạn đang có một phiên mượn chưa hoàn tất." });

                DateTime endDate = borrowingDate.AddDays(1);

                bool roomAlreadyBooked = await _context.phieu_muon
                    .AnyAsync(p => p.ma_phong == room.ma_phong
                        && p.ca_muon == request.CaMuon
                        && p.ngay_muon >= borrowingDate
                        && p.ngay_muon < endDate
                        && ActiveStatuses.Contains(p.trang_thai));

                if (roomAlreadyBooked)
                    return BadRequest(new { message = "Phòng này đã có người đăng ký trong ca học đã chọn." });

                _context.phieu_muon.Add(newBorrowingSession);

                string userRole = User.FindFirst(ClaimTypes.Role)?.Value ?? userAccount.vai_tro;

                _context.nhat_ky_he_thong.Add(new NhatKyHeThong
                {
                    ma_sv = currentUserId,
                    hanh_dong = LogAction.TAO_PHIEU_MUON.ToString(),
                    chi_tiet = $"{userRole} đăng ký mượn thiết bị phòng {room.ma_phong}.",
                    thoi_gian = now,
                    ip_address = Helper.GetClientIp(HttpContext),
                    user_agent = Helper.GetClientOs(Request)
                });

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Không thể tạo phiếu mượn cho người dùng {UserId}.", currentUserId);
                return StatusCode(StatusCodes.Status500InternalServerError, new { message = "Không thể tạo phiên mượn." });
            }

            bool qrDelivered = true;
            try
            {
                await _qrFlow.Issue(newBorrowingSession.id, currentUserId, "RECEIVE");
            }
            catch (Exception ex)
            {
                qrDelivered = false;
                _logger.LogError(ex, "Phiếu {SessionId} đã được tạo nhưng không gửi được QR tới tủ.", newBorrowingSession.id);
            }

            try
            {
                await _hubContext.Clients.All.SendAsync("CabinetStatusChanged", new
                {
                    roomId = newBorrowingSession.ma_phong,
                    isOpen = cabinet.trang_thai_khoa == "UNLOCKED",
                    isOnline = cabinet.trang_thai_mang,
                    lockStatus = cabinet.trang_thai_khoa,
                    status = "Đã đặt",
                    borrower = $"{userAccount.ho_ten} ({currentUserId})",
                    timestamp = DateTime.Now.ToString("HH:mm:ss")
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Không gửi được SignalR sau khi tạo phiếu {SessionId}.", newBorrowingSession.id);
            }

            string message = qrDelivered
                ? "Đăng ký mượn thiết bị thành công. Quét QR trên màn hình tủ để nhận đồ."
                : "Đã tạo phiếu nhưng chưa gửi được QR tới tủ. Hãy cấp lại QR.";

            return StatusCode(StatusCodes.Status201Created, new
            {
                message,
                sessionId = newBorrowingSession.id,
                qrEnabled = newBorrowingSession.QrEnabled,
                qrDelivered
            });
        }

    }
}
