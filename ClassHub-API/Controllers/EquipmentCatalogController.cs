using ClassHub_API.Data;
using ClassHub_API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Data;
using System.Security.Claims;
using System.Text.Json;
namespace ClassHub_API.Controllers;

[ApiController, Authorize(Roles="ADMIN"), Route("api/admin/assets/catalogs")]
public class EquipmentCatalogController(AppDbContext db) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Create(CatalogRequest request)
    {
        if(!Valid(request)) return BadRequest(new {message="Nhập tên, phân loại và cách quản lý hợp lệ."});
        var item=new EquipmentCatalog {Name=request.Name.Trim(),Category=request.Category.Trim(),TrackingMode=request.TrackingMode,Enabled=request.Enabled};
        await using var tx=await db.Database.BeginTransactionAsync();
        db.EquipmentCatalogs.Add(item); await db.SaveChangesAsync();
        Audit("CATALOG_CREATE",item.Id,item.Name,null,null,0,0,0,"Tạo danh mục");
        await db.SaveChangesAsync(); await tx.CommitAsync(); return Ok(item);
    }
    [HttpPatch("{id:int}")]
    public async Task<IActionResult> Update(int id,CatalogRequest request)
    {
        if(!Valid(request)) return BadRequest(new {message="Danh mục không hợp lệ."});
        await using var tx=await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var item=await CatalogLocked(id); if(item==null) return NotFound();
        if(item.Version!=request.Version) return Conflict(new {message="Danh mục đã thay đổi. Làm mới trước khi sửa."});
        if(item.TrackingMode!=request.TrackingMode && await db.thiet_bi.AnyAsync(e=>e.CatalogId==id)) return Conflict(new {message="Không đổi cách quản lý khi đã có bản ghi thiết bị."});
        var before=new {item.Name,item.Category,item.Enabled};
        item.Name=request.Name.Trim(); item.Category=request.Category.Trim(); item.Enabled=request.Enabled; item.TrackingMode=request.TrackingMode; item.Version++;
        foreach(var equipment in await db.thiet_bi.Where(e=>e.CatalogId==id).ToListAsync()) {
            equipment.ten_thiet_bi=item.Name; equipment.Category=item.Category; equipment.Version++;
        }
        Audit("CATALOG_UPDATE",id,item.Name,null,null,0,0,0,JsonSerializer.Serialize(new {before,after=new {item.Name,item.Category,item.Enabled}}));
        await db.SaveChangesAsync(); await tx.CommitAsync(); return Ok(item);
    }
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id,[FromQuery]int version)
    {
        await using var tx=await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var item=await CatalogLocked(id); if(item==null) return NotFound();
        if(item.Version!=version) return Conflict(new {message="Danh mục đã thay đổi. Làm mới trước khi xóa."});
        if(await db.thiet_bi.AnyAsync(e=>e.CatalogId==id && e.so_luong>0)) return Conflict(new {message="Danh mục còn thiết bị trong kho hoặc tại phòng. Có thể ngừng sử dụng, không được xóa."});
        var equipment=await db.thiet_bi.Where(e=>e.CatalogId==id).ToListAsync();
        var equipmentIds=equipment.Select(e=>e.id).ToArray();
        if(await db.BatteryProfiles.AnyAsync(p=>equipmentIds.Contains(p.EquipmentId)))
            return Conflict(new {message="Danh mục còn hồ sơ pin và lịch sử từng chiếc; hãy ngừng sử dụng thay vì xóa."});
        db.thiet_bi.RemoveRange(equipment); db.EquipmentCatalogs.Remove(item);
        Audit("CATALOG_DELETE",id,item.Name,null,null,0,0,0,"Xóa danh mục không còn thiết bị");
        await db.SaveChangesAsync(); await tx.CommitAsync(); return Ok(new {message="Đã xóa danh mục."});
    }

    [HttpPost("/api/admin/assets/rooms/{roomId}/equipment")]
    public async Task<IActionResult> AddToRoom(string roomId,RoomEquipmentRequest request)
    {
        if(string.IsNullOrWhiteSpace(request.Note)) return BadRequest();
        var catalogId=await db.thiet_bi.Where(e=>e.id==request.EquipmentId).Select(e=>(int?)e.CatalogId).FirstOrDefaultAsync();
        if(!catalogId.HasValue) return NotFound();
        await using var tx=await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var catalog=await CatalogLocked(catalogId.Value); if(catalog==null || !catalog.Enabled) return Conflict(new {message="Danh mục đã ngừng sử dụng."});
        var item=await EquipmentLocked(request.EquipmentId); if(item==null) return NotFound();
        if(item.Version!=request.Version) return Conflict(new {message="Tồn kho đã thay đổi. Làm mới và thử lại."});
        if(item.ma_phong!=null || item.tinh_trang!="TOT" || request.Quantity>item.so_luong) return BadRequest(new {message="Chỉ lấy thiết bị sử dụng tốt, không vượt tồn kho."});
        if(!await db.phong_hoc.AnyAsync(r=>r.ma_phong==roomId)) return BadRequest(new {message="Phòng không tồn tại."});
        if(request.Quantity < item.so_luong && await db.BatteryProfiles.AnyAsync(p=>p.EquipmentId==item.id))
            return Conflict(new {message="Không tách nhóm đã có hồ sơ pin từng chiếc khi chưa xác định thiết bị cụ thể."});
        var target=Split(item,request.Quantity); target.ma_phong=roomId; target.ghi_chu=request.Note.Trim();
        Audit("ALLOCATE",catalog.Id,catalog.Name,null,roomId,request.Quantity,item.so_luong,item.so_luong,request.Note);
        await db.SaveChangesAsync(); await tx.CommitAsync(); return Ok(new {message="Đã thêm thiết bị từ kho vào phòng."});
    }

    [HttpPost("/api/admin/assets/{id:int}/room-quantity")]
    public async Task<IActionResult> RoomQuantity(int id,RoomQuantityRequest request)
    {
        if(string.IsNullOrWhiteSpace(request.Note)) return BadRequest();
        var catalogId=await db.thiet_bi.Where(e=>e.id==id).Select(e=>(int?)e.CatalogId).FirstOrDefaultAsync();
        if(!catalogId.HasValue) return NotFound();
        await using var tx=await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var catalog=await CatalogLocked(catalogId.Value); if(catalog==null) return NotFound();
        var item=await EquipmentLocked(id); if(item==null) return NotFound();
        if(item.ma_phong==null) return BadRequest(new {message="Thao tác này chỉ áp dụng cho tài sản tại phòng."});
        if(item.Version!=request.Version) return Conflict(new {message="Tài sản đã thay đổi. Làm mới và thử lại."});
        int before=item.so_luong,delta=request.ActualCount-before;
        if(request.ActualCount < item.so_luong && await db.BatteryProfiles.AnyAsync(p=>p.EquipmentId==item.id))
            return Conflict(new {message="Không tách nhóm đã có hồ sơ pin từng chiếc khi chưa xác định thiết bị cụ thể."});
        if(delta==0 || (item.TrackingMode=="INDIVIDUAL" && request.ActualCount>1)) return BadRequest(new {message="Số lượng mới không hợp lệ hoặc không thay đổi."});
        var room=item.ma_phong;
        if(delta>0) {
            if(!catalog.Enabled || item.TrackingMode!="BULK" || item.tinh_trang!="TOT") return BadRequest(new {message="Chỉ tăng số lượng nhóm thiết bị sử dụng tốt từ danh mục đang hoạt động."});
            var stock=await db.thiet_bi.FromSqlInterpolated($"SELECT * FROM equipment WHERE catalog_id={catalog.Id} AND room_id IS NULL AND `condition`='TOT' AND quantity>0 ORDER BY id FOR UPDATE").ToListAsync();
            // Do not consume a grouped stock row whose physical units have maintenance identities.
            var trackedIds=await db.BatteryProfiles.Select(p=>p.EquipmentId).Distinct().ToListAsync();
            stock=stock.Where(e=>!trackedIds.Contains(e.id)).ToList();
            if(stock.Sum(e=>e.so_luong)<delta) return Conflict(new {message="Không đủ thiết bị sử dụng tốt trong kho."});
            int remaining=delta;
            foreach(var source in stock) {int take=Math.Min(remaining,source.so_luong);source.so_luong-=take;source.Version++;remaining-=take;if(remaining==0) break;}
            item.so_luong=request.ActualCount; item.Version++;
        } else {
            var target=Split(item,-delta); target.ma_phong=null; target.ghi_chu=request.Note.Trim();
        }
        Audit(delta>0?"ALLOCATE":"RETURN",catalog.Id,catalog.Name,delta>0?null:room,delta>0?room:null,Math.Abs(delta),before,request.ActualCount,request.Note);
        await db.SaveChangesAsync(); await tx.CommitAsync(); return Ok(new {message="Đã đối chiếu và cập nhật số lượng kho/phòng."});
    }
    private static bool Valid(CatalogRequest r)=>!string.IsNullOrWhiteSpace(r.Name)&&!string.IsNullOrWhiteSpace(r.Category)&&new[]{"BULK","INDIVIDUAL"}.Contains(r.TrackingMode);
    private async Task<EquipmentCatalog?> CatalogLocked(int id)=>(await db.EquipmentCatalogs.FromSqlInterpolated($"SELECT * FROM equipment_catalog WHERE id={id} FOR UPDATE").ToListAsync()).FirstOrDefault();
    private async Task<ThietBi?> EquipmentLocked(int id)=>(await db.thiet_bi.FromSqlInterpolated($"SELECT * FROM equipment WHERE id={id} FOR UPDATE").ToListAsync()).FirstOrDefault(e=>e.so_luong>0);
    private ThietBi Split(ThietBi source,int quantity) {
        source.Version++; if(source.so_luong==quantity) return source;
        if(source.TrackingMode=="INDIVIDUAL") throw new InvalidOperationException();
        source.so_luong-=quantity;
        var target=new ThietBi {CatalogId=source.CatalogId,CatalogCode=source.CatalogCode,ten_thiet_bi=source.ten_thiet_bi,Category=source.Category,TrackingMode=source.TrackingMode,UsageScope=source.UsageScope,so_luong=quantity,tinh_trang=source.tinh_trang,ma_phong=source.ma_phong,Version=1};
        db.thiet_bi.Add(target); return target;
    }
    private void Audit(string action,int catalogId,string name,string? from,string? to,int quantity,int before,int after,string note)
        =>db.nhat_ky_he_thong.Add(new NhatKyHeThong {ma_sv=User.FindFirstValue(ClaimTypes.NameIdentifier),hanh_dong="ASSET_"+action,
            chi_tiet=JsonSerializer.Serialize(new {catalogId,name,fromRoomId=from,toRoomId=to,quantity,before,after,note}),thoi_gian=DateTime.Now,user_agent="ClassHub Warehouse"});
}
