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
using System.Data;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;

namespace ClassHub_API.Controllers
{
    [Route("api/booking")]
    [ApiController]
    [Authorize]
    public class BookingController : ControllerBase
    {
        private static readonly string[] ActiveStatuses = { "PENDING", "ACTIVE", "IN_USE", "RETURNING" };

        private readonly AppDbContext _context;
        private readonly IMqttService _mqttService;
        private readonly IHubContext<CabinetHub> _hubContext;
        private readonly ILogger<BookingController> _logger;

        public BookingController(AppDbContext context, IMqttService mqttService, IHubContext<CabinetHub> hubContext, ILogger<BookingController> logger)
        {
            _context = context;
            _mqttService = mqttService;
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
            if (cabinet.trang_thai_khoa == "ERROR") return BadRequest(new { message = "Tủ đang gặp lỗi hoặc bảo trì." });

            DateTime now = DateTime.Now;
            string newOtp = RandomNumberGenerator.GetInt32(100000, 1000000).ToString(CultureInfo.InvariantCulture);

            var newBorrowingSession = new PhieuMuon
            {
                ma_sv = currentUserId,
                ma_phong = room.ma_phong,
                ngay_muon = borrowingDate,
                ca_muon = request.CaMuon,
                trang_thai = "PENDING",
                thoi_gian_tao = now,
                otp = newOtp,
                otp_expires_at = now.AddMinutes(3),
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

            bool otpDelivered = true;
            string otpTopic = $"backend/cabinet/{newBorrowingSession.ma_phong}/otp";
            string otpPayload = JsonConvert.SerializeObject(new
            {
                id = newBorrowingSession.id,
                room = newBorrowingSession.ma_phong,
                otp = newOtp
            });

            try
            {
                await _mqttService.PublishAsync(otpTopic, otpPayload);
            }
            catch (Exception ex)
            {
                otpDelivered = false;
                _logger.LogError(ex, "Phiếu {SessionId} đã được tạo nhưng không gửi được OTP tới MQTT.", newBorrowingSession.id);
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

            string message = otpDelivered
                ? "Đăng ký mượn thiết bị thành công."
                : "Đã tạo phiên mượn nhưng HMI chưa nhận được OTP. Hãy cấp lại OTP.";

            return StatusCode(StatusCodes.Status201Created, new
            {
                message,
                sessionId = newBorrowingSession.id,
                otpDelivered,
                otpExpiresAt = newBorrowingSession.otp_expires_at
            });
        }

        [HttpPut("return-room/{id:int}")]
        public async Task<IActionResult> ReturnRoom(int id, [FromQuery] string room)
        {
            string? currentUserId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrWhiteSpace(currentUserId))
                return Unauthorized(new { message = "Không xác định được người dùng." });

            if (id <= 0 || string.IsNullOrWhiteSpace(room))
                return BadRequest(new { message = "Thông tin phiên mượn không hợp lệ." });

            string requestedRoomId = room.Trim();

            var borrowingSession = await _context.phieu_muon
                .FirstOrDefaultAsync(p => p.id == id
                    && p.ma_phong == requestedRoomId
                    && (p.ma_sv == currentUserId || p.ma_sv_uy_quyen == currentUserId));

            if (borrowingSession == null)
                return NotFound(new { message = "Không tìm thấy phiên mượn hoặc bạn không có quyền thao tác." });

            if (borrowingSession.trang_thai == "COMPLETED" || borrowingSession.trang_thai == "CANCELED")
                return BadRequest(new { message = "Phiên mượn đã hoàn tất hoặc đã bị hủy." });

            if (!ActiveStatuses.Contains(borrowingSession.trang_thai))
                return BadRequest(new { message = "Trạng thái phiên mượn không cho phép trả thiết bị." });

            var cabinet = await _context.thiet_bi_iot
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.ma_phong == requestedRoomId);

            if (cabinet == null) return BadRequest(new { message = "Không tìm thấy tủ IoT của phòng." });
            if (!cabinet.trang_thai_mang) return BadRequest(new { message = "Tủ đang offline. Không thể xác nhận trả." });
            if (cabinet.trang_thai_khoa == "ERROR") return BadRequest(new { message = "Tủ đang gặp lỗi hoặc bảo trì." });

            bool isFirstReturnRequest = borrowingSession.trang_thai != "RETURNING";

            if (isFirstReturnRequest)
            {
                borrowingSession.trang_thai = "RETURNING";
                borrowingSession.ma_sv_tra = currentUserId;
                borrowingSession.otp = null;
                borrowingSession.otp_expires_at = null;

                string userRole = User.FindFirst(ClaimTypes.Role)?.Value ?? "SINHVIEN";

                _context.nhat_ky_he_thong.Add(new NhatKyHeThong
                {
                    ma_sv = currentUserId,
                    hanh_dong = "YEU_CAU_TRA_THIET_BI",
                    chi_tiet = $"{userRole} yêu cầu trả thiết bị phòng {requestedRoomId}.",
                    thoi_gian = DateTime.Now,
                    ip_address = Helper.GetClientIp(HttpContext),
                    user_agent = Helper.GetClientOs(Request)
                });

                try
                {
                    await _context.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Không thể chuyển phiếu {SessionId} sang RETURNING.", borrowingSession.id);
                    return StatusCode(StatusCodes.Status500InternalServerError, new { message = "Không thể tạo yêu cầu trả thiết bị." });
                }
            }

            string actionTopic = $"backend/cabinet/{requestedRoomId}/action";
            string actionPayload = JsonConvert.SerializeObject(new
            {
                id = borrowingSession.id,
                room = borrowingSession.ma_phong,
                action = "lock"
            });

            try
            {
                await _mqttService.PublishAsync(actionTopic, actionPayload);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Không gửi được lệnh khóa cho phiếu {SessionId}.", borrowingSession.id);

                return StatusCode(StatusCodes.Status503ServiceUnavailable, new
                {
                    message = "Không gửi được lệnh khóa tới tủ. Hãy thử lại.",
                    status = borrowingSession.trang_thai
                });
            }

            try
            {
                await _hubContext.Clients.All.SendAsync("CabinetStatusChanged", new
                {
                    roomId = requestedRoomId,
                    isOpen = borrowingSession.is_cabinet_open ?? false,
                    isOnline = cabinet.trang_thai_mang,
                    lockStatus = cabinet.trang_thai_khoa,
                    status = "Đang xác nhận trả",
                    timestamp = DateTime.Now.ToString("HH:mm:ss")
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Không gửi được SignalR cho yêu cầu trả phiếu {SessionId}.", borrowingSession.id);
            }

            return Accepted(new
            {
                message = "Đã gửi yêu cầu khóa tủ. Hệ thống đang chờ ESP32 xác nhận.",
                status = "RETURNING"
            });
        }
    }
}