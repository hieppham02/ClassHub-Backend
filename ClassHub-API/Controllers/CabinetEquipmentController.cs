using ClassHub_API.Data;
using ClassHub_API.Models;
using ClassHub_API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;

namespace ClassHub_API.Controllers;

public class EnrollCabinetEquipment {
    [Required, MaxLength(2048)] public string SelectionToken { get; set; } = "";
    [Range(1,int.MaxValue)] public int StockEquipmentId { get; set; }
    [Range(1,int.MaxValue)] public int StockVersion { get; set; }
}

[ApiController, Authorize(Roles="ADMIN"), Route("api/admin/cabinets/{cabinetId:int}/rfid")]
public class CabinetEquipmentController(AppDbContext db, RfidEnrollmentTokens tokens) : ControllerBase {
    [HttpGet("options")]
    public async Task<IActionResult> Options(int cabinetId) {
        var cabinet=await db.thiet_bi_iot.AsNoTracking().FirstOrDefaultAsync(c=>c.id==cabinetId);
        if(cabinet?.ma_phong==null)return NotFound();
        var stock=await db.thiet_bi.AsNoTracking().Where(e=>e.ma_phong==null && e.so_luong>0 && e.tinh_trang=="TOT" &&
            e.Catalog.Enabled && !db.EquipmentRfidTags.Any(t=>t.EquipmentId==e.id) &&
            (e.so_luong==1 || e.TrackingMode!="INDIVIDUAL" && e.AssetCode==null && !db.BatteryProfiles.Any(p=>p.EquipmentId==e.id)))
            .OrderBy(e=>e.ten_thiet_bi).ThenBy(e=>e.id)
            .Select(e=>new {id=e.id,name=e.ten_thiet_bi,quantity=e.so_luong,version=e.Version,assetCode=e.AssetCode}).ToListAsync();
        var bindings=await (from t in db.EquipmentRfidTags.AsNoTracking() join e in db.thiet_bi on t.EquipmentId equals e.id
            where e.ma_phong==cabinet.ma_phong && e.UsageScope=="CABINET"
            orderby e.ten_thiet_bi,t.Uid select new {t.Uid,equipmentId=e.id,name=e.ten_thiet_bi}).ToListAsync();
        return Ok(new {stock,bindings,capacity=32});
    }

    [HttpGet("latest")]
    public async Task<IActionResult> Latest(int cabinetId) {
        Response.Headers.CacheControl="no-store";
        var cabinet=await db.thiet_bi_iot.AsNoTracking().FirstOrDefaultAsync(c=>c.id==cabinetId);
        if(cabinet?.ma_phong==null)return NotFound();
        var health=CabinetHealthService.View(await db.CabinetHealths.AsNoTracking().FirstOrDefaultAsync(h=>h.RoomId==cabinet.ma_phong),DateTime.UtcNow);
        if(!cabinet.trang_thai_mang || health==null || health.Stale)return Ok(new {state="OFFLINE"});
        if(health.Metrics.RfidScanSequence==null)return Ok(new {state="FIRMWARE_UPDATE_REQUIRED"});
        if(health.Quality!="CLOCK_SYNCED")return Ok(new {state="SYNC_REQUIRED"});
        var scan=tokens.Issue(cabinetId,cabinet.ma_phong,health,DateTime.UtcNow);
        if(scan==null)return Ok(new {state="WAITING_FOR_SCAN"});
        if(await db.EquipmentRfidTags.AnyAsync(t=>t.Uid==scan.Uid))return Ok(new {state="ALREADY_BOUND",uid=scan.Uid});
        return Ok(new {state="READY",scan});
    }

    [HttpPost("bind")]
    public async Task<IActionResult> Bind(int cabinetId,EnrollCabinetEquipment request) {
        var actor=User.FindFirstValue(ClaimTypes.NameIdentifier);
        if(string.IsNullOrWhiteSpace(actor))return Unauthorized();
        try {
            await using var tx=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            var catalogId=await db.thiet_bi.Where(e=>e.id==request.StockEquipmentId).Select(e=>(int?)e.CatalogId).FirstOrDefaultAsync();
            if(catalogId==null)return Conflict(new {message="Thiết bị trong kho đã thay đổi. Hãy tải lại danh sách."});
            var catalog=(await db.EquipmentCatalogs.FromSqlInterpolated($"SELECT * FROM equipment_catalog WHERE id={catalogId.Value} FOR UPDATE").ToListAsync()).FirstOrDefault();
            var stock=(await db.thiet_bi.FromSqlInterpolated($"SELECT * FROM equipment WHERE id={request.StockEquipmentId} FOR UPDATE").ToListAsync()).FirstOrDefault();
            var cabinet=(await db.thiet_bi_iot.FromSqlInterpolated($"SELECT * FROM cabinet WHERE id={cabinetId} FOR UPDATE").ToListAsync()).FirstOrDefault();
            if(cabinet?.ma_phong==null)return NotFound();
            var selection=tokens.Read(request.SelectionToken,cabinetId,cabinet.ma_phong,DateTime.UtcNow);
            if(selection==null)return Conflict(new {message="Thẻ đã chọn hết hạn. Hãy quét và chọn lại thẻ."});
            var health=CabinetHealthService.View(await db.CabinetHealths.AsNoTracking().FirstOrDefaultAsync(h=>h.RoomId==cabinet.ma_phong),DateTime.UtcNow);
            if(!cabinet.trang_thai_mang || health==null || health.Stale || health.Metrics.BootId!=selection.BootId)
                return Conflict(new {message="Tủ đã mất kết nối hoặc khởi động lại. Hãy quét lại thẻ."});
            if(await db.phieu_muon.AnyAsync(p=>p.ma_phong==cabinet.ma_phong && new[]{"PENDING","ACTIVE","IN_USE","RETURNING"}.Contains(p.trang_thai)))
                return Conflict(new {message="Tủ đang có phiên mượn/trả. Hãy hoàn tất phiên trước khi thêm thiết bị."});
            if(stock==null || catalog?.Enabled!=true || stock.CatalogId!=catalog.Id || stock.ma_phong!=null ||
                stock.so_luong<1 || stock.tinh_trang!="TOT" || stock.Version!=request.StockVersion)
                return Conflict(new {message="Số lượng hoặc thông tin kho đã thay đổi. Hãy tải lại danh sách."});
            if(await db.EquipmentRfidTags.AnyAsync(t=>t.Uid==selection.Uid || t.EquipmentId==stock.id))
                return Conflict(new {message="Thẻ hoặc thiết bị đã được gắn RFID. Hãy tải lại danh sách."});
            if(await (from t in db.EquipmentRfidTags join e in db.thiet_bi on t.EquipmentId equals e.id
                where e.ma_phong==cabinet.ma_phong && e.UsageScope=="CABINET" select t.Id).CountAsync()>=32)
                return Conflict(new {message="Tủ đã đủ 32 thẻ RFID."});
            ThietBi target=stock;
            if(stock.so_luong>1) {
                if(stock.TrackingMode=="INDIVIDUAL" || stock.AssetCode!=null || await db.BatteryProfiles.AnyAsync(p=>p.EquipmentId==stock.id))
                    return Conflict(new {message="Thiết bị cần được tách riêng trong kho trước khi gắn thẻ."});
                stock.so_luong--;stock.Version++;
                target=new ThietBi {CatalogId=stock.CatalogId,CatalogCode=stock.CatalogCode,ten_thiet_bi=stock.ten_thiet_bi,
                    Category=stock.Category,TrackingMode=stock.TrackingMode,so_luong=1,tinh_trang=stock.tinh_trang,
                    ghi_chu=stock.ghi_chu,Version=1};
                db.thiet_bi.Add(target);
            } else target.Version++;
            target.ma_phong=cabinet.ma_phong;target.UsageScope="CABINET";
            // Obtain the new equipment ID inside the transaction; binding and audit must also succeed before commit.
            await db.SaveChangesAsync();
            db.EquipmentRfidTags.Add(new EquipmentRfidTag {EquipmentId=target.id,Uid=selection.Uid});
            db.nhat_ky_he_thong.Add(new NhatKyHeThong {ma_sv=actor,hanh_dong="RFID_STOCK_TO_CABINET",
                chi_tiet=System.Text.Json.JsonSerializer.Serialize(new {stockEquipmentId=stock.id,cabinetId,roomId=cabinet.ma_phong,quantity=1,uid=selection.Uid}),
                user_agent="ClassHub RFID",thoi_gian=DateTime.Now});
            await db.SaveChangesAsync();await tx.CommitAsync();
            return Ok(new {uid=selection.Uid,equipmentId=target.id,message="Đã chuyển 1 thiết bị vào tủ và gắn thẻ RFID."});
        } catch(DbUpdateException) {
            return Conflict(new {message="Kho hoặc thẻ đã thay đổi. Hãy tải lại trước khi thực hiện tiếp."});
        } catch(MySqlConnector.MySqlException ex) when(ex.Number is 1205 or 1213) {
            return Conflict(new {message="Kho đang được cập nhật. Hãy tải lại danh sách và thử lại."});
        }
    }
}
