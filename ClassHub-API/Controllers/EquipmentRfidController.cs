using ClassHub_API.Data;
using ClassHub_API.Models;
using ClassHub_API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;

namespace ClassHub_API.Controllers;
public class BindRfidRequest { [Required, MaxLength(20)] public string Uid { get; set; } = ""; }

[ApiController, Authorize(Roles = "ADMIN"), Route("api/operations/equipment/{id:int}/rfid")]
public class EquipmentRfidController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(int id) => Ok(await db.EquipmentRfidTags.AsNoTracking()
        .Where(t => t.EquipmentId == id).OrderBy(t => t.Uid).Select(t => new { t.Uid, t.CreatedAtUtc }).ToListAsync());

    [HttpPost]
    public async Task<IActionResult> Add(int id, BindRfidRequest request)
    {
        var uid = request.Uid.Trim().ToUpperInvariant();
        if (!RfidSession.ValidUid(uid)) return BadRequest(new { message = "UID phải gồm 8, 14 hoặc 20 ký tự hex." });
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        var equipment = await db.thiet_bi.FirstOrDefaultAsync(e => e.id == id && e.UsageScope == "CABINET");
        if (equipment == null) return NotFound();
        if (await db.EquipmentRfidTags.AnyAsync(t => t.Uid == uid)) return Conflict(new { message = "UID đã được gắn với thiết bị khác." });
        if (await db.EquipmentRfidTags.CountAsync(t => t.EquipmentId == id) >= equipment.so_luong)
            return Conflict(new { message = "Số thẻ đã bằng số lượng thiết bị. Kiểm tra danh mục hoặc tháo thẻ cũ." });
        if (await (from t in db.EquipmentRfidTags join e in db.thiet_bi on t.EquipmentId equals e.id
                   where e.ma_phong == equipment.ma_phong && e.UsageScope == "CABINET" select t.Id).CountAsync() >= 32)
            return Conflict(new { message = "Mỗi tủ hỗ trợ tối đa 32 thẻ." });
        db.EquipmentRfidTags.Add(new EquipmentRfidTag { EquipmentId = id, Uid = uid });
        db.nhat_ky_he_thong.Add(new NhatKyHeThong { ma_sv = User.FindFirstValue(ClaimTypes.NameIdentifier),
            hanh_dong = "RFID_TAG_BIND", chi_tiet = $"Thiết bị #{id}, UID {uid}", user_agent = "ClassHub RFID", thoi_gian = DateTime.Now });
        try { await db.SaveChangesAsync(); await tx.CommitAsync(); }
        catch (DbUpdateException) { return Conflict(new { message = "Không thể gắn UID; kiểm tra thẻ đã tồn tại hoặc danh mục thay đổi." }); }
        return Ok(new { uid });
    }

    [HttpDelete("{uid}")]
    public async Task<IActionResult> Delete(int id, string uid)
    {
        uid = uid.Trim().ToUpperInvariant();
        if (!RfidSession.ValidUid(uid)) return BadRequest();
        var tag = await db.EquipmentRfidTags.FirstOrDefaultAsync(t => t.EquipmentId == id && t.Uid == uid);
        if (tag == null) return NotFound();
        db.EquipmentRfidTags.Remove(tag);
        db.nhat_ky_he_thong.Add(new NhatKyHeThong { ma_sv = User.FindFirstValue(ClaimTypes.NameIdentifier),
            hanh_dong = "RFID_TAG_UNBIND", chi_tiet = $"Thiết bị #{id}, UID {uid}", user_agent = "ClassHub RFID", thoi_gian = DateTime.Now });
        await db.SaveChangesAsync();
        return NoContent();
    }
}
