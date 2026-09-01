using ClassHub_API.Data;
using ClassHub_API.DTOs;
using ClassHub_API.Hubs;
using ClassHub_API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using System.Security.Claims;

namespace ClassHub_API.Controllers
{
    [Route("api/cabinet")]
    [ApiController]
    [Authorize]
    public class CabinetController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly IMqttService _mqttService;
        private readonly IHubContext<CabinetHub> _hubContext;

        public CabinetController(
            AppDbContext context,
            IMqttService mqttService,
            IHubContext<CabinetHub> hubContext)
        {
            _context = context;
            _mqttService = mqttService;
            _hubContext = hubContext;
        }

        [HttpPost("open-door/{id}")]
        public async Task<IActionResult> OpenDoor(int id, [FromBody] OpenDoorDTO request)
        {
            var maSv = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(maSv)) return Unauthorized();

            var phieu = await _context.phieu_muon
                .FirstOrDefaultAsync(p => p.id == id && (p.ma_sv == maSv || p.ma_sv_uy_quyen == maSv));
            if (phieu == null) return NotFound(new { message = "Không tìm thấy phiếu mượn!" });

            if (phieu.trang_thai == "COMPLETED" || phieu.trang_thai == "CANCELED")
            {
                return BadRequest(new { message = "Phòng này đã trả hoặc đã hủy, không thể mở tủ!" });
            }

            if (phieu.otp != request.Otp)
            {
                return BadRequest(new { message = "Mã OTP không chính xác!" });
            }

            if (phieu.trang_thai == "PENDING")
            {
                phieu.trang_thai = "ACTIVE";
                phieu.thoi_gian_nhan = DateTime.Now;
            }
            phieu.is_cabinet_open = true;

            var cabinet = await _context.thiet_bi_iot.FirstOrDefaultAsync(c => c.ma_phong == phieu.ma_phong);
            if (cabinet != null)
            {
                cabinet.trang_thai_khoa = "UNLOCKED";
                cabinet.lan_cuoi_online = DateTime.Now;
            }

            await _context.SaveChangesAsync();

            // GỬI LỆNH MỞ TỦ ĐẾN TOPIC: backend/cabinet/{ma_phong}/action
            string topic = $"backend/cabinet/{phieu.ma_phong}/action";
            var obj = new { id = phieu.id, room = phieu.ma_phong, action = "open" };
            string payload = JsonConvert.SerializeObject(obj);

            await _mqttService.PublishAsync(topic, payload);
            await _mqttService.PublishAsync("tu_thiet_bi/ACTION", payload);

            await _hubContext.Clients.All.SendAsync("CabinetStatusChanged", new
            {
                roomId = phieu.ma_phong,
                isOpen = true,
                doorCondition = "Mở",
                isOnline = true,
                lockStatus = "UNLOCKED",
                timestamp = DateTime.Now.ToString("HH:mm:ss")
            });

            return Ok(new { message = "Gửi yêu cầu mở cửa thành công!" });
        }
    }
}