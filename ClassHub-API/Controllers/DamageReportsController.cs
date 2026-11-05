using ClassHub_API.Data;
using ClassHub_API.Models;
using ClassHub_API.Services;
using ClassHub_API.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;

namespace ClassHub_API.Controllers;

[ApiController, Authorize, Route("api/damage-reports")]
public partial class DamageReportsController(AppDbContext context) : ControllerBase
{
    public class CreateRequest
    {
        public int? EquipmentId { get; set; }
        public int? SessionId { get; set; }
        [Required, StringLength(50)] public string RoomId { get; set; } = "";
        [Required, StringLength(100)] public string EquipmentName { get; set; } = "";
        [Required, StringLength(2000, MinimumLength = 10)] public string Description { get; set; } = "";
    }
    public class ResolveRequest
    {
        [Required, StringLength(1000, MinimumLength = 3)] public string Note { get; set; } = "";
    }
    public class ReportDetail : DamageReportDetail { }

    [HttpPost]
    public async Task<IActionResult> Create(CreateRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();
        if (!await context.tai_khoan.AnyAsync(a => a.ma_sv == userId && a.trang_thai)) return Forbid();
        if (!await context.phong_hoc.AnyAsync(r => r.ma_phong == request.RoomId))
            return BadRequest(new { message = "Vị trí báo hỏng không tồn tại." });
        if (request.EquipmentName.Trim().Length == 0 || request.Description.Trim().Length < 10)
            return BadRequest(new { message = "Nhập tên thiết bị và mô tả ít nhất 10 ký tự." });
        if (request.SessionId.HasValue && !await context.phieu_muon.AnyAsync(s => s.id == request.SessionId && s.ma_phong == request.RoomId &&
            (s.ma_sv == userId || s.ma_sv_uy_quyen == userId))) return BadRequest(new { message = "Phiên liên quan không thuộc bạn hoặc không đúng vị trí." });
        if (request.EquipmentId.HasValue) {
            var equipment = await context.thiet_bi.FirstOrDefaultAsync(e => e.id == request.EquipmentId && e.ma_phong == request.RoomId);
            if (equipment == null) return BadRequest(new { message = "Thiết bị không thuộc vị trí báo cáo." });
            request.EquipmentName = equipment.ten_thiet_bi;
        }
        var report = new NhatKyHeThong {
            ma_sv = userId, hanh_dong = "DAMAGE_REPORT", thoi_gian = DateTime.Now,
            chi_tiet = JsonSerializer.Serialize(new ReportDetail { RoomId = request.RoomId,
                EquipmentName = request.EquipmentName.Trim(), Description = request.Description.Trim(),
                EquipmentId = request.EquipmentId, SessionId = request.SessionId,
                History = [new("CREATED", userId, DateTime.UtcNow, "Tiếp nhận báo cáo")] }),
            user_agent = "ClassHub Damage Reports"
        };
        context.nhat_ky_he_thong.Add(report);
        await context.SaveChangesAsync();
        return StatusCode(201, new { report.id, message = "Đã gửi báo cáo hỏng hóc cho quản trị viên." });
    }

    [HttpGet, Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> List()
    {
        var reports = await context.nhat_ky_he_thong.AsNoTracking()
            .Where(r => r.hanh_dong == "DAMAGE_REPORT")
            .OrderByDescending(r => r.thoi_gian).ThenByDescending(r => r.id).ToListAsync();
        return Ok(reports.Select(r => {
            var detail = JsonSerializer.Deserialize<ReportDetail>(r.chi_tiet ?? "{}")!;
            return new { r.id, reporterId = r.ma_sv, createdAt = r.thoi_gian,
                detail.RoomId, detail.EquipmentName, detail.Description, detail.Status,
                detail.ResolutionNote, detail.ResolvedBy, detail.ResolvedAt, detail.AssigneeId,
                detail.EquipmentId, detail.SessionId, detail.IncidentType, detail.AllocationBlocked, detail.History };
        }));
    }

    [HttpPatch("{id:int}/resolve"), Authorize(Roles = "ADMIN")]
    public Task<IActionResult> Resolve(int id, ResolveRequest request) => Workflow(id,
        new WorkflowRequest { Status = "RESOLVED", Note = request.Note });
}
