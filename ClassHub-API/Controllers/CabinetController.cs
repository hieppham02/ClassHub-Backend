using ClassHub_API.Data;
using ClassHub_API.DTOs;
using ClassHub_API.Models;
using ClassHub_API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using System.Data;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace ClassHub_API.Controllers
{
    [Route("api/cabinet")]
    [ApiController]
    [Authorize]
    public class CabinetController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly IMqttService _mqttService;
        private readonly ILogger<CabinetController> _logger;

        public CabinetController(AppDbContext context, IMqttService mqttService, ILogger<CabinetController> logger)
        {
            _context = context;
            _mqttService = mqttService;
            _logger = logger;
        }

        [HttpPost("open-door/{id:int}")]
        public async Task<IActionResult> OpenDoor(int id, [FromBody] OpenDoorDTO request)
        {
            string? currentUserId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            if (string.IsNullOrWhiteSpace(currentUserId))
            {
                return Unauthorized(new
                {
                    message = "Không xác định được người dùng."
                });
            }

            if (request == null || string.IsNullOrWhiteSpace(request.Otp))
            {
                return BadRequest(new
                {
                    message = "Vui lòng nhập mã OTP."
                });
            }

            string providedOtp = request.Otp.Trim();

            if (providedOtp.Length != 6 || providedOtp.Any(character => !char.IsDigit(character)))
            {
                return BadRequest(new
                {
                    message = "Mã OTP phải gồm đúng 6 chữ số."
                });
            }

            await using var transaction = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable);

            var borrowingSession = await _context.phieu_muon.FirstOrDefaultAsync(session => session.id == id && (session.ma_sv == currentUserId || session.ma_sv_uy_quyen == currentUserId));

            if (borrowingSession == null)
            {
                return NotFound(new
                {
                    message = "Không tìm thấy phiếu mượn hoặc bạn không có quyền mở tủ."
                });
            }

            string[] validStatuses =
            {
                "PENDING",
                "ACTIVE",
                "IN_USE"
            };

            if (!validStatuses.Contains(borrowingSession.trang_thai))
            {
                return Conflict(new
                {
                    message = "Phiếu mượn không còn ở trạng thái được phép mở tủ."
                });
            }

            var cabinet = await _context.thiet_bi_iot
                .FirstOrDefaultAsync(item => item.ma_phong == borrowingSession.ma_phong);

            if (cabinet == null)
            {
                return NotFound(new
                {
                    message = "Phòng này chưa được gán tủ thông minh."
                });
            }

            if (!cabinet.trang_thai_mang)
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new
                {
                    message = "Tủ đang ngoại tuyến. Vui lòng thử lại sau."
                });
            }

            if (cabinet.trang_thai_khoa == "ERROR")
            {
                return Conflict(new
                {
                    message = "Tủ đang gặp lỗi hoặc được bảo trì."
                });
            }

            if (string.IsNullOrWhiteSpace(borrowingSession.otp))
            {
                return Conflict(new
                {
                    message = "Mã OTP đã được sử dụng. Vui lòng yêu cầu cấp mã mới."
                });
            }

            DateTime currentTime = DateTime.Now;

            if (!borrowingSession.otp_expires_at.HasValue || borrowingSession.otp_expires_at.Value <= currentTime)
            {
                borrowingSession.otp = null;
                borrowingSession.otp_expires_at = null;

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                return BadRequest(new
                {
                    message = "Mã OTP đã hết hạn. Vui lòng yêu cầu cấp mã mới."
                });
            }

            if (!OtpMatches(providedOtp, borrowingSession.otp))
            {
                return BadRequest(new
                {
                    message = "Mã OTP không chính xác."
                });
            }

            string commandId = Guid.NewGuid().ToString("N");

            borrowingSession.otp = null;
            borrowingSession.otp_expires_at = null;

            _context.nhat_ky_he_thong.Add(new NhatKyHeThong
            {
                ma_sv = currentUserId,
                hanh_dong = "YEU_CAU_MO_TU",
                chi_tiet = $"Yêu cầu mở tủ phòng {borrowingSession.ma_phong}, " + $"phiếu mượn #{borrowingSession.id}, " + $"commandId: {commandId}",
                thoi_gian = currentTime,
                ip_address = Helper.GetClientIp(HttpContext),
                user_agent = Helper.GetClientOs(Request)
            });

            await _context.SaveChangesAsync();
            await transaction.CommitAsync();

            string topic = $"backend/cabinet/{borrowingSession.ma_phong}/action";

            var commandPayload = new
            {
                id = borrowingSession.id.ToString(),
                room = borrowingSession.ma_phong,
                action = "open",
                ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };

            string payload = JsonConvert.SerializeObject(commandPayload);

            try
            {
                await _mqttService.PublishAsync(topic, payload);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Không thể gửi lệnh mở tủ {RoomId}, commandId {CommandId}", borrowingSession.ma_phong, commandId);

                return StatusCode(StatusCodes.Status503ServiceUnavailable, new
                {
                    message = "Không thể gửi lệnh đến tủ. " + "Vui lòng cấp lại OTP và thử lại.",
                    commandId
                });
            }

            return Accepted(new
            {
                message = "Đã gửi yêu cầu mở tủ. " + "Hệ thống đang chờ thiết bị xác nhận.",
                commandId
            });
        }

        private static bool OtpMatches(string providedOtp, string storedOtp)
        {
            byte[] providedBytes = Encoding.UTF8.GetBytes(providedOtp);
            byte[] storedBytes = Encoding.UTF8.GetBytes(storedOtp);
            return providedBytes.Length == storedBytes.Length && CryptographicOperations.FixedTimeEquals(providedBytes, storedBytes);
        }
    }
}