using ClassHub_API.Data; // Đổi lại thành namespace DbContext của ông (VD: ClassHub_API.Models nếu cần)
using ClassHub_API.DTOs;
using ClassHub_API.Models;
using ClassHub_API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using System.Security.Claims;

namespace ClassHub_API.Controllers
{
    [Route("api/history")]
    [ApiController]
    [Authorize]
    public class HistoryController : Controller
    {
        // Thay AppDbContext bằng DbContext thực tế của ông nếu khác tên
        private readonly AppDbContext _context;
        private readonly IMqttService _mqttService;

        public HistoryController(AppDbContext context, IMqttService mqttService)
        {
            _context = context;
            _mqttService = mqttService;
        }

        [HttpGet("get-history")]
        public IActionResult GetHistory()
        {
            var maSv = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(maSv)) return Unauthorized();

            var rawData = (from p in _context.phieu_muon
                           where p.ma_sv == maSv || p.ma_sv_uy_quyen == maSv
                           orderby p.thoi_gian_tao descending
                           join ph in _context.phong_hoc on p.ma_phong equals ph.ma_phong into phGroup
                           from ph in phGroup.DefaultIfEmpty()
                           join tn in _context.toa_nha on ph.ma_toa_nha equals tn.ma_toa_nha into tnGroup
                           from tn in tnGroup.DefaultIfEmpty()
                           join tkMuon in _context.tai_khoan on p.ma_sv equals tkMuon.ma_sv into tkMuonGroup
                           from tkMuon in tkMuonGroup.DefaultIfEmpty()
                           join tkTra in _context.tai_khoan on p.ma_sv_tra equals tkTra.ma_sv into tkTraGroup
                           from tkTra in tkTraGroup.DefaultIfEmpty()
                           select new
                           {
                               p.id,
                               p.ma_phong,
                               TenPhong = ph != null ? ph.ten_phong : p.ma_phong,
                               TenToaNha = tn != null ? tn.ten_toa_nha : "EAUT",
                               p.ngay_muon,
                               p.ca_muon,
                               p.trang_thai,
                               p.thoi_gian_tao,
                               p.thoi_gian_tra,
                               TenNguoiMuon = tkMuon != null ? tkMuon.ho_ten : p.ma_sv,
                               TenNguoiTra = tkTra != null ? tkTra.ho_ten : p.ma_sv_tra,
                               p.is_cabinet_open
                           }).ToList();

            var history = rawData.Select(p => new
            {
                id = p.id,
                room = p.ma_phong,
                name = p.TenPhong,
                building = p.TenToaNha,
                date = p.ngay_muon.HasValue ? p.ngay_muon.Value.ToString("dd-MM-yyyy") : "",
                slot = "Ca " + p.ca_muon,
                status = p.trang_thai,
                borrowerName = p.TenNguoiMuon,
                returnerName = p.TenNguoiTra,
                createdAt = p.thoi_gian_tao.ToString("HH:mm"),
                returnedAt = p.thoi_gian_tra.HasValue ? p.thoi_gian_tra.Value.ToString("HH:mm") : "---",
                isCabinetOpen = p.is_cabinet_open == 1
            }).ToList();

            return Ok(history);
        }

        [HttpPost("refresh-otp/{id}")]
        public async Task<IActionResult> RefreshOtp(int id)
        {
            var maSv = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(maSv)) return Unauthorized();

            var phieu = _context.phieu_muon.FirstOrDefault(p => p.id == id && (p.ma_sv == maSv || p.ma_sv_uy_quyen == maSv));
            if (phieu == null) return NotFound(new { message = "Không tìm thấy phiếu mượn hoặc bạn không có quyền!" });

            if (phieu.trang_thai == "COMPLETED" || phieu.trang_thai == "CANCELED")
                return BadRequest(new { message = "Phòng đã trả hoặc hủy, không thể cấp lại OTP!" });

            Random rnd = new Random();
            string newOtp = rnd.Next(100000, 999999).ToString();

            phieu.otp = newOtp;
            await _context.SaveChangesAsync();

            string topic = $"tu_thiet_bi/OTP";
            var obj = new { id = phieu.id, room = phieu.ma_phong, otp = newOtp, };
            string payload = JsonConvert.SerializeObject(obj);

            await _mqttService.PublishAsync(topic, payload);
            return Ok(new { message = "Đã cấp lại mã OTP mới cho thiết bị!" });
        }

        [HttpPost("delegate/{id}")]
        public async Task<IActionResult> DelegateAccess(int id, [FromBody] DelegateDTO req)
        {
            var maSv = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(maSv)) return Unauthorized();

            // Chỉ người mượn gốc mới có quyền đi ủy quyền
            var phieu = _context.phieu_muon.FirstOrDefault(p => p.id == id && p.ma_sv == maSv);
            if (phieu == null) return NotFound(new { message = "Không tìm thấy phiếu hoặc bạn không phải người mượn gốc!" });

            var userDelegate = _context.tai_khoan.FirstOrDefault(t => t.ma_sv == req.DelegateId);
            if (userDelegate == null) return BadRequest(new { message = "Mã sinh viên này không tồn tại trong hệ thống!" });

            if (userDelegate.ma_sv == maSv) return BadRequest(new { message = "Không thể tự ủy quyền cho chính mình!" });

            phieu.ma_sv_uy_quyen = req.DelegateId;
            await _context.SaveChangesAsync();

            return Ok(new { message = $"Đã ủy quyền thành công cho: {userDelegate.ho_ten}!" });
        }

        [HttpPost("revoke/{id}")]
        public async Task<IActionResult> RevokeAccess(int id)
        {
            var maSv = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(maSv)) return Unauthorized();

            var phieu = _context.phieu_muon.FirstOrDefault(p => p.id == id && p.ma_sv == maSv);
            if (phieu == null) return NotFound(new { message = "Không tìm thấy phiếu hoặc bạn không có quyền!" });

            phieu.ma_sv_uy_quyen = null;
            await _context.SaveChangesAsync();
            return Ok(new { message = "Đã thu hồi quyền thành công!" });
        }
    }
}