using ClassHub_API.Data;
using ClassHub_API.DTOs;
using ClassHub_API.Models;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace ClassHub_API.Services;
public static class AllocationPolicy
{
    public static DamageReportDetail? Parse(string? json) {
        try { return JsonSerializer.Deserialize<DamageReportDetail>(json ?? "{}", new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch (JsonException) { return null; }
    }
    public static async Task<HashSet<string>> BlockedRooms(AppDbContext db) {
        var details = await db.nhat_ky_he_thong.FromSqlRaw("SELECT * FROM audit_log WHERE action='DAMAGE_REPORT' AND JSON_UNQUOTE(JSON_EXTRACT(IF(JSON_VALID(details),details,JSON_OBJECT()),'$.AllocationBlocked'))='true'")
            .AsNoTracking().Select(r => r.chi_tiet).ToListAsync();
        return details.Select(Parse).Where(d => d != null && !string.IsNullOrEmpty(d.RoomId)).Select(d => d!.RoomId).ToHashSet(StringComparer.Ordinal);
    }
    public static async Task<bool> IsBlocked(AppDbContext db, string room) {
        return await db.nhat_ky_he_thong.FromSqlInterpolated($"SELECT * FROM audit_log WHERE action='DAMAGE_REPORT' AND JSON_UNQUOTE(JSON_EXTRACT(IF(JSON_VALID(details),details,'{{}}'),'$.RoomId'))={room} AND JSON_UNQUOTE(JSON_EXTRACT(IF(JSON_VALID(details),details,'{{}}'),'$.AllocationBlocked'))='true'")
            .AsNoTracking().AnyAsync();
    }
    public static async Task MarkReturnIncident(AppDbContext db, PhieuMuon session, string condition, string? note, string? actor) {
        if (condition is not ("MAT_DO" or "HONG_HOC")) return;
        var kind = condition == "MAT_DO" ? "MISSING" : "DAMAGE";
        var reports = await db.nhat_ky_he_thong.FromSqlInterpolated($"SELECT * FROM audit_log WHERE action='DAMAGE_REPORT' AND JSON_UNQUOTE(JSON_EXTRACT(IF(JSON_VALID(details),details,'{{}}'),'$.RoomId'))={session.ma_phong}").ToListAsync();
        var existing = reports.FirstOrDefault(r => { var d = Parse(r.chi_tiet); return d?.SessionId == session.id && d.IncidentType == kind; });
        var detail = existing == null ? new DamageReportDetail { RoomId = session.ma_phong, SessionId = session.id,
            EquipmentName = "Bộ thiết bị bàn giao", Description = note?.Trim() ?? "Kiểm tra lại thiết bị của phiên trả.", IncidentType = kind } : Parse(existing.chi_tiet)!;
        detail.AllocationBlocked = true;
        if (detail.Status == "RESOLVED") detail.Status = "PENDING";
        detail.AssigneeId ??= actor;
        detail.History.Add(new("RETURN_REVIEW", actor, DateTime.UtcNow, note?.Trim() ?? condition));
        if (existing == null) {
            existing = new NhatKyHeThong { ma_sv = actor, hanh_dong = "DAMAGE_REPORT", thoi_gian = DateTime.Now, user_agent = "ClassHub Return Review" };
            db.nhat_ky_he_thong.Add(existing);
        }
        existing.chi_tiet = JsonSerializer.Serialize(detail);
    }
}
