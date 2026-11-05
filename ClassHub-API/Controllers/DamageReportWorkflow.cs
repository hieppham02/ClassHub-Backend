using ClassHub_API.Services;
using ClassHub_API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;

namespace ClassHub_API.Controllers;
public partial class DamageReportsController
{
    public class WorkflowRequest {
        [MaxLength(50)] public string? AssigneeId { get; set; }
        public bool Unassign { get; set; }
        public string? Status { get; set; }
        public bool? AllocationBlocked { get; set; }
        public bool VerifiedReady { get; set; }
        public int? EquipmentId { get; set; }
        public int? SessionId { get; set; }
        [Required, StringLength(1000, MinimumLength = 3)] public string Note { get; set; } = "";
    }
    [HttpGet("assignees"), Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> Assignees() => Ok(await context.tai_khoan.AsNoTracking()
        .Where(a => a.trang_thai && a.vai_tro == "ADMIN").OrderBy(a => a.ho_ten).Select(a => new { id = a.ma_sv, name = a.ho_ten }).ToListAsync());

    [HttpPatch("{id:int}/workflow"), Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> Workflow(int id, WorkflowRequest request)
    {
        var actor = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (actor == null || !await context.tai_khoan.AnyAsync(a => a.ma_sv == actor && a.trang_thai && a.vai_tro == "ADMIN")) return Forbid();
        if (request.Note.Trim().Length is < 3 or > 1000) return BadRequest(new { message = "Ghi kết quả/thao tác từ 3 đến 1000 ký tự." });
        await using var tx = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        var report = (await context.nhat_ky_he_thong.FromSqlInterpolated($"SELECT * FROM audit_log WHERE id={id} FOR UPDATE").ToListAsync()).SingleOrDefault();
        if (report == null || report.hanh_dong != "DAMAGE_REPORT") return NotFound();
        var detail = AllocationPolicy.Parse(report.chi_tiet);
        if (detail == null) return Conflict(new { message = "Dữ liệu báo cáo cần được kiểm tra." });
        var status = request.Status ?? detail.Status;
        if (status is not ("PENDING" or "IN_PROGRESS" or "RESOLVED")) return BadRequest(new { message = "Trạng thái xử lý không hợp lệ." });
        if (request.Unassign && !string.IsNullOrWhiteSpace(request.AssigneeId)) return BadRequest();
        var assignee = request.Unassign ? null : request.AssigneeId?.Trim() ?? detail.AssigneeId;
        if (status is "IN_PROGRESS" or "RESOLVED" && string.IsNullOrEmpty(assignee)) assignee = actor;
        if (assignee != null && !await context.tai_khoan.AnyAsync(a => a.ma_sv == assignee && a.trang_thai && a.vai_tro == "ADMIN"))
            return BadRequest(new { message = "Người phụ trách phải là quản trị viên đang hoạt động." });
        if (request.EquipmentId.HasValue && !await context.thiet_bi.AnyAsync(e => e.id == request.EquipmentId && e.ma_phong == detail.RoomId))
            return BadRequest(new { message = "Thiết bị không thuộc vị trí báo cáo." });
        if (request.SessionId.HasValue && !await context.phieu_muon.AnyAsync(s => s.id == request.SessionId && s.ma_phong == detail.RoomId))
            return BadRequest(new { message = "Phiên không thuộc vị trí báo cáo." });
        var cabinet = (await context.thiet_bi_iot.FromSqlInterpolated($"SELECT * FROM cabinet WHERE room_id={detail.RoomId} FOR UPDATE").ToListAsync()).FirstOrDefault();
        var block = request.AllocationBlocked ?? detail.AllocationBlocked;
        if (detail.AllocationBlocked && !block)
        {
            if (status != "RESOLVED" || !request.VerifiedReady) return Conflict(new { message = "Chỉ gỡ chặn khi sự cố đã xử lý và đã xác minh sẵn sàng cấp phát." });
            if (await context.phieu_muon.AnyAsync(s => s.ma_phong == detail.RoomId && (s.trang_thai == "ACTIVE" || s.trang_thai == "IN_USE" || s.trang_thai == "RETURNING" || s.is_cabinet_open == true)))
                return Conflict(new { message = "Còn phiên đang mượn/trả hoặc cửa đang mở; chưa thể gỡ chặn." });
            var health = await context.CabinetHealths.AsNoTracking().FirstOrDefaultAsync(h => h.RoomId == detail.RoomId);
            var measured = CabinetHealthService.View(health, DateTime.UtcNow);
            if (cabinet != null && (!cabinet.trang_thai_mang || cabinet.trang_thai_khoa == "UNLOCKED" || measured?.Metrics.DoorOpen == true))
                return Conflict(new { message = "Cần tủ online, cửa đóng và chốt không mở trước khi gỡ chặn." });
            if (await context.thiet_bi.AnyAsync(e => e.ma_phong == detail.RoomId && e.UsageScope == "CABINET" && e.so_luong > 0 && e.tinh_trang != "TOT"))
                return Conflict(new { message = "Còn thiết bị trong tủ chưa được xác nhận tốt." });
        }
        var previous = new { detail.Status, detail.AssigneeId, detail.AllocationBlocked };
        detail.Status = status; detail.AssigneeId = assignee; detail.AllocationBlocked = block;
        detail.EquipmentId = request.EquipmentId ?? detail.EquipmentId; detail.SessionId = request.SessionId ?? detail.SessionId;
        if (status == "RESOLVED") { detail.ResolutionNote = request.Note.Trim(); detail.ResolvedBy = actor; detail.ResolvedAt = DateTime.UtcNow; }
        detail.History.Add(new("WORKFLOW", actor, DateTime.UtcNow, request.Note.Trim()));
        report.chi_tiet = JsonSerializer.Serialize(detail);
        context.nhat_ky_he_thong.Add(new NhatKyHeThong { ma_sv = actor, hanh_dong = "INCIDENT_WORKFLOW", thoi_gian = DateTime.Now,
            user_agent = "ClassHub Incidents", chi_tiet = JsonSerializer.Serialize(new { reportId = id, detail.RoomId, previous,
                status, assignee, allocationBlocked = block, request.VerifiedReady, request.Note }) });
        await context.SaveChangesAsync(); await tx.CommitAsync();
        var stillBlocked = await AllocationPolicy.IsBlocked(context, detail.RoomId);
        return Ok(new { message = stillBlocked ? "Đã cập nhật. Vị trí vẫn bị chặn cấp phát cho đến khi được xác minh và gỡ chặn." : "Đã cập nhật xử lý sự cố.", roomStillBlocked = stillBlocked });
    }
}
