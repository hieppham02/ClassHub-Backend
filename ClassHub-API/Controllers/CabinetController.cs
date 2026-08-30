using ClassHub_API.Data;
using ClassHub_API.DTOs;
using ClassHub_API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using System.Security.Claims;

namespace ClassHub_API.Controllers
{
    [Route("api/cabinet")]
    [ApiController]
    [Authorize]
    public class CabinetController : Controller
    {
        private readonly AppDbContext _context;
        private readonly IMqttService _mqttService;

        public CabinetController(AppDbContext context, IMqttService mqttService)
        {
            _context = context;
            _mqttService = mqttService;
        }

        [HttpPost("open-door/{id}")]
        public async Task<IActionResult> OpenDoor(int id, [FromBody] OpenDoorDTO request)
        {
            var maSv = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(maSv)) return Unauthorized();

            var phieu = _context.phieu_muon.FirstOrDefault(p => p.id == id && p.ma_sv == maSv);
            if (phieu == null) return NotFound(new { message = "Không tìm thấy phiếu mượn!" });

            if (phieu.trang_thai == "COMPLETED" || phieu.trang_thai == "CANCELED")
            {
                return BadRequest(new { message = "Phòng này đã trả, không thể mở tủ!" });
            }

            if (phieu.otp != request.Otp)
            {
                return BadRequest(new { message = "Mã OTP không chính xác!" });
            }

            string topic = $"tu_thiet_bi/ACTION";
            var Obj = new
            {
                id = phieu.id,
                room = phieu.ma_phong,
                action = "open"
                
            };
            string payload = JsonConvert.SerializeObject(Obj);

            await _mqttService.PublishAsync(topic, payload);

            return Ok(new { message = "Gửi yêu cầu mở cửa thành công!" });
        }
    }
}
