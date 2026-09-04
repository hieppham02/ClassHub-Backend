using ClassHub_API.Data;
using ClassHub_API.DTOs;
using ClassHub_API.Models;
using ClassHub_API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using System.Security.Claims;
using System.Security.Cryptography;

namespace ClassHub_API.Controllers
{
    [Route("api/history")]
    [ApiController]
    [Authorize]
    public class HistoryController : ControllerBase
    {
        private static readonly string[] ActiveStatuses = { "PENDING", "ACTIVE", "IN_USE" };

        private readonly AppDbContext _context;
        private readonly IMqttService _mqttService;
        private readonly ILogger<HistoryController> _logger;

        public HistoryController(AppDbContext context, IMqttService mqttService, ILogger<HistoryController> logger)
        {
            _context = context;
            _mqttService = mqttService;
            _logger = logger;
        }

        [HttpGet("get-history")]
        public async Task<IActionResult> GetHistory()
        {
            string? currentUserId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            if (string.IsNullOrWhiteSpace(currentUserId))
            {
                return Unauthorized(new { message = "Không xác định được người dùng." });
            }

            var rawData = await (
                from borrowingSession in _context.phieu_muon.AsNoTracking()
                where borrowingSession.ma_sv == currentUserId || borrowingSession.ma_sv_uy_quyen == currentUserId
                orderby borrowingSession.thoi_gian_tao descending

                join room in _context.phong_hoc on borrowingSession.ma_phong equals room.ma_phong into roomGroup
                from room in roomGroup.DefaultIfEmpty()

                join building in _context.toa_nha on room.ma_toa_nha equals building.ma_toa_nha into buildingGroup
                from building in buildingGroup.DefaultIfEmpty()

                join borrower in _context.tai_khoan on borrowingSession.ma_sv equals borrower.ma_sv into borrowerGroup
                from borrower in borrowerGroup.DefaultIfEmpty()

                join returner in _context.tai_khoan on borrowingSession.ma_sv_tra equals returner.ma_sv into returnerGroup
                from returner in returnerGroup.DefaultIfEmpty()

                select new
                {
                    borrowingSession.id,
                    borrowingSession.ma_phong,
                    RoomName = room != null ? room.ten_phong : borrowingSession.ma_phong,
                    BuildingName = building != null ? building.ten_toa_nha : "Chưa xác định",
                    borrowingSession.ngay_muon,
                    borrowingSession.ca_muon,
                    borrowingSession.trang_thai,
                    borrowingSession.thoi_gian_tao,
                    borrowingSession.thoi_gian_tra,
                    BorrowerName = borrower != null ? borrower.ho_ten : borrowingSession.ma_sv,
                    ReturnerName = returner != null ? returner.ho_ten : borrowingSession.ma_sv_tra,
                    borrowingSession.is_cabinet_open
                }).ToListAsync();

            var history = rawData.Select(item => new
            {
                id = item.id,
                room = item.ma_phong,
                name = item.RoomName,
                building = item.BuildingName,
                date = item.ngay_muon.ToString("dd-MM-yyyy"),
                slot = $"Ca {item.ca_muon}",
                status = item.trang_thai,
                borrowerName = item.BorrowerName,
                returnerName = item.ReturnerName,
                createdAt = item.thoi_gian_tao.ToString("HH:mm"),
                returnedAt = item.thoi_gian_tra?.ToString("HH:mm") ?? "---",
                isCabinetOpen = item.is_cabinet_open == true
            }).ToList();

            return Ok(history);
        }

        [HttpPost("refresh-otp/{id:int}")]
        public async Task<IActionResult> RefreshOtp(int id)
        {
            string? currentUserId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            if (string.IsNullOrWhiteSpace(currentUserId))
            {
                return Unauthorized(new { message = "Không xác định được người dùng." });
            }

            var borrowingSession = await _context.phieu_muon.FirstOrDefaultAsync(session =>
                session.id == id &&
                (session.ma_sv == currentUserId || session.ma_sv_uy_quyen == currentUserId));

            if (borrowingSession == null)
            {
                return NotFound(new { message = "Không tìm thấy phiếu mượn hoặc bạn không có quyền cấp lại OTP." });
            }

            if (!ActiveStatuses.Contains(borrowingSession.trang_thai))
            {
                return Conflict(new { message = "Phiếu mượn không còn ở trạng thái cấp OTP." });
            }

            var cabinet = await _context.thiet_bi_iot.FirstOrDefaultAsync(item => item.ma_phong == borrowingSession.ma_phong);

            if (cabinet == null)
            {
                return NotFound(new { message = "Phòng này chưa được gán tủ." });
            }

            if (!cabinet.trang_thai_mang)
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new
                {
                    message = "Tủ đang ngoại tuyến nên chưa thể cấp OTP."
                });
            }

            if (cabinet.trang_thai_khoa == "ERROR")
            {
                return Conflict(new { message = "Tủ đang gặp lỗi hoặc được bảo trì." });
            }

            string newOtp = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
            DateTime expiresAt = DateTime.Now.AddMinutes(3);

            borrowingSession.otp = newOtp;
            borrowingSession.otp_expires_at = expiresAt;

            _context.nhat_ky_he_thong.Add(new NhatKyHeThong
            {
                ma_sv = currentUserId,
                hanh_dong = "CAP_LAI_OTP",
                chi_tiet = $"Cấp lại OTP cho phiếu #{borrowingSession.id}, phòng {borrowingSession.ma_phong}",
                thoi_gian = DateTime.Now,
                ip_address = Helper.GetClientIp(HttpContext),
                user_agent = Helper.GetClientOs(Request)
            });

            await _context.SaveChangesAsync();

            string topic = $"backend/cabinet/{borrowingSession.ma_phong}/otp";

            var otpPayload = new
            {
                id = borrowingSession.id.ToString(),
                room = borrowingSession.ma_phong,
                otp = newOtp
            };

            string payload = JsonConvert.SerializeObject(otpPayload);

            try
            {
                await _mqttService.PublishAsync(topic, payload);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Không thể gửi OTP tới tủ phòng {RoomId}", borrowingSession.ma_phong);

                return StatusCode(StatusCodes.Status503ServiceUnavailable, new
                {
                    message = "OTP đã được tạo nhưng chưa gửi được tới tủ. Vui lòng thử cấp lại."
                });
            }

            return Ok(new
            {
                message = "Đã cấp mã OTP mới.",
                expiresAt
            });
        }

        [HttpPost("delegate/{id:int}")]
        public async Task<IActionResult> DelegateAccess(int id, [FromBody] DelegateDTO request)
        {
            string? currentUserId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            if (string.IsNullOrWhiteSpace(currentUserId))
            {
                return Unauthorized(new { message = "Không xác định được người dùng." });
            }

            if (request == null || string.IsNullOrWhiteSpace(request.DelegateId))
            {
                return BadRequest(new { message = "Vui lòng nhập mã người được ủy quyền." });
            }

            string delegateId = request.DelegateId.Trim();

            var borrowingSession = await _context.phieu_muon.FirstOrDefaultAsync(session =>
                session.id == id && session.ma_sv == currentUserId);

            if (borrowingSession == null)
            {
                return NotFound(new { message = "Không tìm thấy phiếu hoặc bạn không phải người mượn gốc." });
            }

            if (!ActiveStatuses.Contains(borrowingSession.trang_thai))
            {
                return Conflict(new { message = "Phiếu mượn không còn ở trạng thái được phép ủy quyền." });
            }

            if (delegateId == currentUserId)
            {
                return BadRequest(new { message = "Không thể tự ủy quyền cho chính mình." });
            }

            var delegatedUser = await _context.tai_khoan.AsNoTracking()
                .FirstOrDefaultAsync(user => user.ma_sv == delegateId);

            if (delegatedUser == null)
            {
                return NotFound(new { message = "Người được ủy quyền không tồn tại." });
            }

            if (!delegatedUser.trang_thai)
            {
                return Conflict(new { message = "Tài khoản được ủy quyền đang bị khóa." });
            }

            borrowingSession.ma_sv_uy_quyen = delegatedUser.ma_sv;

            _context.nhat_ky_he_thong.Add(new NhatKyHeThong
            {
                ma_sv = currentUserId,
                hanh_dong = "UY_QUYEN",
                chi_tiet = $"Ủy quyền phiếu #{borrowingSession.id} cho {delegatedUser.ma_sv}",
                thoi_gian = DateTime.Now,
                ip_address = Helper.GetClientIp(HttpContext),
                user_agent = Helper.GetClientOs(Request)
            });

            await _context.SaveChangesAsync();

            return Ok(new
            {
                message = $"Đã ủy quyền thành công cho {delegatedUser.ho_ten}."
            });
        }

        [HttpPost("revoke/{id:int}")]
        public async Task<IActionResult> RevokeAccess(int id)
        {
            string? currentUserId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            if (string.IsNullOrWhiteSpace(currentUserId))
            {
                return Unauthorized(new { message = "Không xác định được người dùng." });
            }

            var borrowingSession = await _context.phieu_muon.FirstOrDefaultAsync(session =>
                session.id == id && session.ma_sv == currentUserId);

            if (borrowingSession == null)
            {
                return NotFound(new { message = "Không tìm thấy phiếu hoặc bạn không phải người mượn gốc." });
            }

            if (!ActiveStatuses.Contains(borrowingSession.trang_thai))
            {
                return Conflict(new { message = "Phiếu mượn không còn ở trạng thái được phép thu hồi quyền." });
            }

            if (string.IsNullOrWhiteSpace(borrowingSession.ma_sv_uy_quyen))
            {
                return Conflict(new { message = "Phiếu mượn hiện không có người được ủy quyền." });
            }

            string revokedUserId = borrowingSession.ma_sv_uy_quyen;
            borrowingSession.ma_sv_uy_quyen = null;

            _context.nhat_ky_he_thong.Add(new NhatKyHeThong
            {
                ma_sv = currentUserId,
                hanh_dong = "THU_HOI_UY_QUYEN",
                chi_tiet = $"Thu hồi quyền của {revokedUserId} trên phiếu #{borrowingSession.id}",
                thoi_gian = DateTime.Now,
                ip_address = Helper.GetClientIp(HttpContext),
                user_agent = Helper.GetClientOs(Request)
            });

            await _context.SaveChangesAsync();

            return Ok(new { message = "Đã thu hồi quyền thành công." });
        }
    }
}