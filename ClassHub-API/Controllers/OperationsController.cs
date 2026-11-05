using ClassHub_API.Data;
using ClassHub_API.Models;
using ClassHub_API.Services;
using ClassHub_API.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;

namespace ClassHub_API.Controllers;

public class EquipmentUpdate
{
    [Range(1, 10000)] public int Quantity { get; set; }
    [Required] public string Condition { get; set; } = "TOT";
    [MaxLength(255)] public string? Note { get; set; }
}

public class ReturnReport
{
    public int? InspectionId { get; set; }
    public bool PhysicalCheck { get; set; }
    public List<CheckedReturnItem>? CheckedItems { get; set; }
    [Required] public string Condition { get; set; } = "BINH_THUONG";
    [MaxLength(2000)] public string? Note { get; set; }
}

[ApiController]
[Authorize]
[Route("api/operations")]
public class OperationsController(AppDbContext context, IHubContext<CabinetHub> hub) : ControllerBase
{
    [HttpGet("slots")]
    public async Task<IActionResult> Slots()
    {
        var slots = await context.cau_hinh_ca_hoc.AsNoTracking().OrderBy(s => s.ca_id).ToListAsync();
        return Ok(slots.Select(s => new { id = s.ca_id, name = s.ten_ca,
            start = s.gio_bat_dau.ToString(@"hh\:mm"), end = s.gio_ket_thuc.ToString(@"hh\:mm") }));
    }

    [Authorize(Roles = "ADMIN")]
    [HttpGet("equipment")]
    public async Task<IActionResult> Equipment() => Ok(await context.thiet_bi.AsNoTracking().Where(e => e.UsageScope == "CABINET" && e.so_luong > 0)
        .OrderBy(e => e.ma_phong).ThenBy(e => e.id)
        .Select(e => new { e.id, room = e.ma_phong, name = e.ten_thiet_bi,
            quantity = e.so_luong, condition = e.tinh_trang, note = e.ghi_chu }).ToListAsync());

    [Authorize(Roles = "ADMIN")]
    [HttpPatch("equipment/{id:int}")]
    public async Task<IActionResult> UpdateEquipment(int id, EquipmentUpdate request)
    {
        if (!new[] { "TOT", "HONG", "DANG_BAO_TRI" }.Contains(request.Condition))
            return BadRequest(new { message = "Tình trạng thiết bị không hợp lệ." });
        var item = await context.thiet_bi.FindAsync(id);
        if (item == null) return NotFound();
        if (request.Quantity != item.so_luong)
            return Conflict(new { message = "Số lượng chỉ thay đổi qua nhập kho, phân bổ hoặc kiểm kê tài sản." });
        if (item.UsageScope != "CABINET")
            return Conflict(new { message = "Cập nhật tài sản phòng qua trang Quản lý tài sản." });
        item.Version++;
        item.tinh_trang = request.Condition;
        item.ghi_chu = request.Note?.Trim();
        Log("CAP_NHAT_THIET_BI", $"Thiết bị #{id}: {request.Quantity}, {request.Condition}.");
        await context.SaveChangesAsync();
        return NoContent();
    }

    [HttpPut("sessions/{id:int}/return-report")]
    public async Task<IActionResult> Report(int id, ReturnReport request)
    {
        if (!new[] { "BINH_THUONG", "HONG_HOC", "MAT_DO" }.Contains(request.Condition))
            return BadRequest(new { message = "Tình trạng trả không hợp lệ." });
        if (request.Condition != "BINH_THUONG" && string.IsNullOrWhiteSpace(request.Note))
            return BadRequest(new { message = "Vui lòng mô tả thiết bị hỏng hoặc thiếu." });
        string? userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var session = await context.phieu_muon.FirstOrDefaultAsync(s => s.id == id &&
            (s.ma_sv == userId || s.ma_sv_uy_quyen == userId));
        if (session == null) return NotFound();
        if (!new[] { "ACTIVE", "IN_USE" }.Contains(session.trang_thai))
            return Conflict(new { message = "Chỉ ghi nhận tình trạng cho phiên đang mượn." });
        session.tinh_trang_tra = request.Condition;
        session.ghi_chu_tra = request.Note?.Trim();
        Log("BAO_CAO_TINH_TRANG_TRA", $"Phiếu #{id}: {request.Condition}. {session.ghi_chu_tra}");
        await context.SaveChangesAsync();
        return NoContent();
    }

    [Authorize(Roles = "ADMIN")]
    [HttpPost("sessions/{id:int}/confirm-return")]
    public async Task<IActionResult> Confirm(int id, ReturnReport request)
    {
        if (!new[] { "BINH_THUONG", "HONG_HOC", "MAT_DO" }.Contains(request.Condition) ||
            (request.Condition != "BINH_THUONG" && string.IsNullOrWhiteSpace(request.Note)))
            return BadRequest(new { message = "Tình trạng hoặc ghi chú không hợp lệ." });
        await using var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        var session = await context.phieu_muon.FirstOrDefaultAsync(s => s.id == id);
        if (session == null) return NotFound();
        if (session.trang_thai == "COMPLETED") return Ok(new { message = "Phiếu đã được xác nhận." });
        if (session.trang_thai != "RETURNING") return Conflict(new { message = "Phiếu chưa ở bước xác nhận trả." });
        var cabinet = await context.thiet_bi_iot.FirstOrDefaultAsync(c => c.ma_phong == session.ma_phong);
        if (cabinet == null || !cabinet.trang_thai_mang || cabinet.trang_thai_khoa != "LOCKED")
            return Conflict(new { message = "Tủ phải online và đã khóa trước khi xác nhận." });
        var qr = await context.QrAccesses.OrderByDescending(q => q.Id).FirstOrDefaultAsync(q => q.SessionId == id);
        if (session.QrEnabled && (qr?.Purpose != "RETURN" || qr.State != "CLOSED" || session.is_cabinet_open == true))
            return Conflict(new { message = "Chờ tủ xác nhận đã mở rồi đóng cửa của đúng phiên trả." });
        var command = qr?.CommandId;
        var inspection = await context.ReturnInspections.OrderByDescending(i => i.Id).FirstOrDefaultAsync(i =>
            i.SessionId == id && i.CommandId == command);
        if (inspection == null || inspection.Id != request.InspectionId)
            return Conflict(new { message = "Cần kết quả kiểm tra mới nhất của đúng phiên trả. Hãy chụp ảnh hộc trước khi xác nhận." });
        if (inspection.ImageJpeg == null && !request.PhysicalCheck)
            return Conflict(new { message = "Chưa có ảnh: quản trị viên cần xác nhận đã trực tiếp kiểm tra hộc." });
        if (string.IsNullOrWhiteSpace(request.Note))
            return BadRequest(new { message = "Ghi rõ kết quả đối chiếu ảnh và thiết bị trước khi xác nhận." });
        var expected = await context.phieu_muon_thiet_bi.Where(e => e.phieu_muon_id == id).OrderBy(e => e.id)
            .Select(e => new ExpectedReturnItem(e.id, e.thiet_bi_id, e.ten_thiet_bi, e.so_luong)).ToListAsync();
        var capturedExpected = System.Text.Json.JsonSerializer.Deserialize<List<ExpectedReturnItem>>(inspection.ExpectedJson);
        if (capturedExpected == null || !expected.SequenceEqual(capturedExpected))
            return Conflict(new { message = "Danh sách thiết bị đã thay đổi sau khi chụp; cần chụp lại ảnh kiểm tra." });
        if (request.Condition != "MAT_DO" && !ReturnReviewRules.CompleteChecklist(expected, request.CheckedItems))
            return BadRequest(new { message = "Đối chiếu và nhập đủ số lượng trả cho từng thiết bị của phiếu." });
        inspection.ReviewedBy = User.FindFirstValue(ClaimTypes.NameIdentifier);
        inspection.ReviewedAtUtc = DateTime.UtcNow;
        inspection.ReviewJson = System.Text.Json.JsonSerializer.Serialize(new { request.Condition, request.Note, request.CheckedItems, request.PhysicalCheck, hasImage = inspection.ImageJpeg != null });
        if (request.Condition == "MAT_DO")
        {
            await AllocationPolicy.MarkReturnIncident(context, session, request.Condition, request.Note, User.FindFirstValue(ClaimTypes.NameIdentifier));
            session.tinh_trang_tra = request.Condition;
            session.QrPhase = "RETURN_FAILED";
            session.ghi_chu_tra = request.Note?.Trim();
            Log("XAC_NHAN_THIEU_THIET_BI", $"Phiếu #{id}: {session.ghi_chu_tra}");
            await context.SaveChangesAsync();
            await transaction.CommitAsync();
            return Ok(new { message = "Đã ghi nhận thiếu đồ; phiếu tiếp tục chờ xử lý." });
        }
        session.tinh_trang_tra = request.Condition;
        session.ghi_chu_tra = request.Note?.Trim();
        session.trang_thai = "COMPLETED";
        session.QrPhase = "COMPLETED";
        session.thoi_gian_tra = DateTime.Now;
        session.is_cabinet_open = false;
        if (request.Condition == "HONG_HOC") await AllocationPolicy.MarkReturnIncident(context, session, request.Condition, request.Note, User.FindFirstValue(ClaimTypes.NameIdentifier));
        Log("XAC_NHAN_TRA_THIET_BI", $"Admin kiểm tra phiếu #{id}, bằng chứng #{inspection.Id}, SHA256 {inspection.ImageSha256 ?? "không có ảnh"}, kiểm tra trực tiếp {request.PhysicalCheck}: {request.Condition}. {session.ghi_chu_tra}");
        await context.SaveChangesAsync();
        await transaction.CommitAsync();
        var stillBlocked = await AllocationPolicy.IsBlocked(context, session.ma_phong);
        await hub.Clients.All.SendAsync("CabinetStatusChanged", new { roomId = session.ma_phong,
            sessionId = id, isOpen = false, isOnline = true, qrPhase = "COMPLETED",
            status = stillBlocked ? "Bảo trì" : "Trống" });
        return Ok(new { message = request.Condition == "HONG_HOC"
            ? "Đã xác nhận trả. Tủ được chặn cấp phát để kiểm tra thiết bị hỏng." : "Đã xác nhận trả đủ thiết bị." });
    }

    [HttpGet("alerts")]
    public async Task<IActionResult> Alerts()
    {
        string? userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var query = context.phieu_muon.AsNoTracking().Where(s =>
            s.trang_thai == "ACTIVE" || s.trang_thai == "IN_USE" || s.trang_thai == "RETURNING");
        if (!User.IsInRole("ADMIN")) query = query.Where(s => s.ma_sv == userId || s.ma_sv_uy_quyen == userId);
        var rows = await (from s in query join slot in context.cau_hinh_ca_hoc on s.ca_muon equals slot.ca_id
            select new { s.id, s.ma_phong, s.ngay_muon, s.ca_muon, s.trang_thai,
                s.tinh_trang_tra, s.ghi_chu_tra, slot.gio_bat_dau, slot.gio_ket_thuc }).ToListAsync();
        var now = DateTime.Now;
        var result = rows.Select(s => {
            var due = s.ngay_muon.Date.Add(s.gio_ket_thuc)
                .AddDays(s.gio_ket_thuc < s.gio_bat_dau ? 1 : 0).AddMinutes(15);
            return new { sessionId = s.id, room = s.ma_phong, status = s.trang_thai,
                condition = s.tinh_trang_tra, note = s.ghi_chu_tra, dueAt = due,
                isOverdue = now > due, needsReview = s.trang_thai == "RETURNING" };
        }).Where(s => s.isOverdue || s.needsReview).OrderBy(s => s.dueAt);
        return Ok(result);
    }

    private void Log(string action, string detail) => context.nhat_ky_he_thong.Add(new NhatKyHeThong {
        ma_sv = User.FindFirstValue(ClaimTypes.NameIdentifier), hanh_dong = action,
        chi_tiet = detail, thoi_gian = DateTime.Now, user_agent = "ClassHub Operations" });
}
