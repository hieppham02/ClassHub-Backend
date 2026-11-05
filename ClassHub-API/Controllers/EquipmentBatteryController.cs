using ClassHub_API.Data;
using ClassHub_API.DTOs;
using ClassHub_API.Models;
using ClassHub_API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Data;
using System.Security.Claims;
using System.Text.Json;
namespace ClassHub_API.Controllers;

[ApiController, Authorize(Roles="ADMIN"), Route("api/admin/batteries")]
public class EquipmentBatteryController(AppDbContext db, BatteryAssessmentService assessment) : ControllerBase {
    static string? Clean(string? value)=>string.IsNullOrWhiteSpace(value)?null:value.Trim();
    static DateTime Utc(DateTime value)=>DateTime.SpecifyKind(value,DateTimeKind.Utc);
    static object Decision(BatteryDecision d)=>new {d.Priority,d.Reason,d.RecommendedAction,
        evidenceAtUtc=d.EvidenceAtUtc.HasValue?(DateTime?)Utc(d.EvidenceAtUtc.Value):null,
        validUntilUtc=d.ValidUntilUtc.HasValue?(DateTime?)Utc(d.ValidUntilUtc.Value):null,method="RULE",methodVersion=BatteryRules.Version};
    static object Profile(EquipmentBatteryProfile p)=>new {p.Id,p.EquipmentId,p.AssetCode,p.CurrentRfidTagId,p.BatteryType,p.BatteryCount,
        p.Rechargeable,p.Manufacturer,p.Model,p.Enabled,p.Version,createdAtUtc=Utc(p.CreatedAtUtc),updatedAtUtc=Utc(p.UpdatedAtUtc)};
    void Audit(string action,long profileId)=>db.nhat_ky_he_thong.Add(new NhatKyHeThong {
        ma_sv=User.FindFirstValue(ClaimTypes.NameIdentifier),hanh_dong="BATTERY_"+action,
        chi_tiet=JsonSerializer.Serialize(new {profileId,source="REAL"}),thoi_gian=DateTime.Now,user_agent="ClassHub Battery"});
    async Task<bool> Actor()=>await db.tai_khoan.AnyAsync(x=>x.ma_sv==User.FindFirstValue(ClaimTypes.NameIdentifier) && x.trang_thai);
    async Task<ThietBi?> EquipmentLocked(int id)=>(await db.thiet_bi.FromSqlInterpolated(
        $"SELECT * FROM equipment WHERE id={id} FOR UPDATE").ToListAsync()).FirstOrDefault();
    async Task<EquipmentBatteryProfile?> Locked(long id) {
        var equipmentId=await db.BatteryProfiles.Where(p=>p.Id==id).Select(p=>(int?)p.EquipmentId).FirstOrDefaultAsync();
        if(!equipmentId.HasValue)return null;
        await EquipmentLocked(equipmentId.Value); // Same lock ordering as profile creation.
        return (await db.BatteryProfiles.FromSqlInterpolated(
            $"SELECT * FROM equipment_battery_profile WHERE id={id} FOR UPDATE").ToListAsync()).FirstOrDefault();
    }
    async Task<bool> TagValid(int? id,int equipmentId,long? profileId) {
        if(!id.HasValue)return true;
        var tag=(await db.EquipmentRfidTags.FromSqlInterpolated(
            $"SELECT * FROM equipment_rfid_tag WHERE id={id.Value} FOR UPDATE").ToListAsync()).FirstOrDefault();
        return tag?.EquipmentId==equipmentId && !await db.BatteryProfiles.AnyAsync(p=>p.CurrentRfidTagId==id && p.Id!=profileId);
    }
    static void Assign(EquipmentBatteryProfile p,BatteryProfileRequest r,DateTime now) {
        p.AssetCode=r.AssetCode; p.CurrentRfidTagId=r.CurrentRfidTagId; p.BatteryType=Clean(r.BatteryType);
        p.BatteryCount=r.BatteryCount; p.Rechargeable=r.Rechargeable; p.Manufacturer=Clean(r.Manufacturer);
        p.Model=Clean(r.Model);p.Enabled=r.Enabled;p.UpdatedAtUtc=now;
    }
    [HttpGet("options")]
    public async Task<IActionResult> Options() {
        var equipment=await db.thiet_bi.AsNoTracking().Where(e=>e.UsageScope=="CABINET" && e.so_luong>0)
            .OrderBy(e=>e.ten_thiet_bi).Select(e=>new {e.id,name=e.ten_thiet_bi,roomId=e.ma_phong,quantity=e.so_luong,
                registeredCount=db.BatteryProfiles.Count(p=>p.EquipmentId==e.id)}).ToListAsync();
        var tags=await db.EquipmentRfidTags.AsNoTracking().Select(t=>new {t.Id,t.EquipmentId,t.Uid,
            profileId=db.BatteryProfiles.Where(p=>p.CurrentRfidTagId==t.Id).Select(p=>(long?)p.Id).FirstOrDefault()}).ToListAsync();
        return Ok(new {equipment,tags});
    }
    [HttpGet]
    public async Task<IActionResult> List([FromQuery]string? room=null,[FromQuery]string? search=null,
        [FromQuery]string? priority=null,[FromQuery]bool includeDisabled=false,[FromQuery]int page=1) {
        if(page<1 || page>10000 || room?.Length>50 || search?.Length>80 ||
            priority!=null && !new[]{"HIGH","MEDIUM","LOW","UNKNOWN"}.Contains(priority))return BadRequest();
        var query=from p in db.BatteryProfiles.AsNoTracking()
                  join e in db.thiet_bi.AsNoTracking() on p.EquipmentId equals e.id
                  join t in db.EquipmentRfidTags.AsNoTracking() on p.CurrentRfidTagId equals (int?)t.Id into tagRows
                  from t in tagRows.DefaultIfEmpty()
                  where (includeDisabled || p.Enabled) && (room==null || e.ma_phong==room) &&
                    (search==null || p.AssetCode.Contains(search) || e.ten_thiet_bi.Contains(search) || t!=null && t.Uid.Contains(search))
                  select new {Profile=p,equipmentName=e.ten_thiet_bi,roomId=e.ma_phong,uid=t==null?null:t.Uid};
        var rows=await query.OrderBy(x=>x.Profile.Id).Take(1001).ToListAsync();
        if(rows.Count>1000)return BadRequest(new {message="Có hơn 1.000 hồ sơ trong phạm vi này. Chọn phòng hoặc tìm mã micro để thu hẹp."});
        var now=DateTime.UtcNow;
        var evaluated=await assessment.Evaluate(rows.Select(x=>x.Profile).ToList(),now);
        int Rank(string p)=>p=="HIGH"?0:p=="MEDIUM"?1:p=="UNKNOWN"?2:3;
        var filtered=rows.Where(x=>priority==null || evaluated[x.Profile.Id].Decision.Priority==priority)
            .OrderBy(x=>Rank(evaluated[x.Profile.Id].Decision.Priority)).ThenBy(x=>x.Profile.AssetCode).ToList();
        return Ok(new {items=filtered.Skip((page-1)*25).Take(25).Select(x=>new {profile=Profile(x.Profile),x.equipmentName,x.roomId,x.uid,
            assessment=Decision(evaluated[x.Profile.Id].Decision)}),total=filtered.Count,page,pageSize=25,
            inspectionFreshHours=assessment.FreshHours,assessedAtUtc=now,source="REAL",method="RULE"});
    }
    [HttpGet("{id:long}")]
    public async Task<IActionResult> Detail(long id) {
        var p=await db.BatteryProfiles.AsNoTracking().FirstOrDefaultAsync(x=>x.Id==id);
        if(p==null)return NotFound();
        var e=await db.thiet_bi.AsNoTracking().FirstAsync(x=>x.id==p.EquipmentId);
        var uid=await db.EquipmentRfidTags.Where(x=>x.Id==p.CurrentRfidTagId).Select(x=>x.Uid).FirstOrDefaultAsync();
        var value=(await assessment.Evaluate([p],DateTime.UtcNow))[id];
        return Ok(new {profile=Profile(p),equipmentName=e.ten_thiet_bi,roomId=e.ma_phong,uid,assessment=Decision(value.Decision)});
    }
    [HttpPost]
    public async Task<IActionResult> Create(BatteryProfileRequest r) {
        if(!await Actor())return Unauthorized();
        await using var tx=await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var e=await EquipmentLocked(r.EquipmentId);
        if(e==null || e.UsageScope!="CABINET" || e.so_luong<1)return BadRequest(new {message="Chọn micro trong danh mục thiết bị tủ."});
        if(await db.BatteryProfiles.CountAsync(p=>p.EquipmentId==e.id)>=e.so_luong)
            return Conflict(new {message="Số hồ sơ đã bằng số lượng thiết bị, gồm cả hồ sơ ngừng theo dõi."});
        if(!await TagValid(r.CurrentRfidTagId,e.id,null))return BadRequest(new {message="Thẻ phải thuộc thiết bị đã chọn và chưa gắn hồ sơ khác."});
        var now=DateTime.UtcNow;
        var p=new EquipmentBatteryProfile {EquipmentId=e.id,CreatedAtUtc=now};
        Assign(p,r,now);db.BatteryProfiles.Add(p);
        try {
            await db.SaveChangesAsync();await assessment.Store(p,now);Audit("PROFILE_CREATE",p.Id);
            await db.SaveChangesAsync();await tx.CommitAsync();return Ok(new {profile=Profile(p)});
        }catch(DbUpdateException){return Conflict(new {message="Không lưu được hồ sơ. Mã micro hoặc RFID có thể đã được dùng; tải lại trước khi thử lại."});}
    }
    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id,BatteryProfileRequest r) {
        if(!await Actor())return Unauthorized();
        await using var tx=await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var p=await Locked(id);if(p==null)return NotFound();
        if(p.Version!=r.Version || p.Version==int.MaxValue)return Conflict(new {message="Hồ sơ đã thay đổi. Tải lại trước khi sửa."});
        if(r.EquipmentId!=p.EquipmentId)return BadRequest(new {message="Không chuyển hồ sơ sang nhóm thiết bị khác."});
        if(!await TagValid(r.CurrentRfidTagId,p.EquipmentId,p.Id))return BadRequest(new {message="RFID không thuộc thiết bị hoặc đã có hồ sơ khác."});
        var now=DateTime.UtcNow;Assign(p,r,now);p.Version++;
        try {
            await db.SaveChangesAsync();await assessment.Store(p,now);Audit("PROFILE_UPDATE",p.Id);
            await db.SaveChangesAsync();await tx.CommitAsync();return Ok(new {profile=Profile(p)});
        }catch(DbUpdateException){return Conflict(new {message="Không lưu được hồ sơ. Tải lại để kiểm tra mã và thẻ bị trùng."});}
    }
    [HttpGet("{id:long}/inspections")]
    public async Task<IActionResult> Inspections(long id,[FromQuery]long? before=null) {
        if(before is <=0)return BadRequest();
        if(!await db.BatteryProfiles.AnyAsync(x=>x.Id==id))return NotFound();
        var items=await db.BatteryInspections.AsNoTracking().Where(x=>x.ProfileId==id && x.DataSource=="REAL" && (!before.HasValue || x.Id<before))
            .OrderByDescending(x=>x.Id).Take(51).ToListAsync();
        return Ok(new {items=items.Take(50).Select(x=>new {x.Id,observedAtUtc=Utc(x.ObservedAtUtc),recordedAtUtc=Utc(x.RecordedAtUtc),
            x.BatteryState,x.OperationState,x.VoltageV,x.BatteryPercent,x.MeasurementMethod,x.SymptomCode,x.Note,x.SessionId,x.RecordedBy}),
            nextBefore=items.Count>50?(long?)items[49].Id:null});
    }
    [HttpPost("{id:long}/inspections")]
    public async Task<IActionResult> Inspect(long id,BatteryInspectionRequest r) {
        if(!await Actor())return Unauthorized();
        var now=DateTime.UtcNow;var time=r.ObservedAt?.UtcDateTime ?? now;
        if(time>now || time.Year<2000)return BadRequest(new {message="Thời điểm kiểm tra phải hợp lệ và không ở tương lai."});
        if((r.VoltageV!=null || r.BatteryPercent!=null) && Clean(r.MeasurementMethod)==null)
            return BadRequest(new {message="Ghi cách đo khi nhập điện áp hoặc phần trăm pin; không tự suy ra phần trăm từ điện áp."});
        if(r.BatteryState=="UNKNOWN" && r.OperationState=="UNKNOWN" && r.VoltageV==null && r.BatteryPercent==null && Clean(r.Note)==null)
            return BadRequest(new {message="Nhập ít nhất một kết quả kiểm tra hoặc ghi chú."});
        await using var tx=await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var p=await Locked(id);if(p==null)return NotFound();
        if(!p.Enabled || p.Version!=r.Version || p.Version==int.MaxValue)return Conflict(new {message="Hồ sơ đã đổi hoặc ngừng theo dõi. Tải lại trước khi ghi."});
        if(r.SessionId.HasValue && !await db.phieu_muon_thiet_bi.AnyAsync(x=>x.phieu_muon_id==r.SessionId && x.thiet_bi_id==p.EquipmentId && x.so_luong>0))
            return BadRequest(new {message="Phiên tham chiếu không có thiết bị này trong danh sách dự kiến."});
        var row=new EquipmentBatteryInspection {ProfileId=id,ObservedAtUtc=time,RecordedAtUtc=now,BatteryState=r.BatteryState,
            OperationState=r.OperationState,VoltageV=r.VoltageV,BatteryPercent=r.BatteryPercent,MeasurementMethod=Clean(r.MeasurementMethod),
            SymptomCode=Clean(r.SymptomCode),Note=Clean(r.Note),SessionId=r.SessionId,
            RecordedBy=User.FindFirstValue(ClaimTypes.NameIdentifier),DataSource="REAL"};
        db.BatteryInspections.Add(row);p.Version++;p.UpdatedAtUtc=now;
        try {
            await db.SaveChangesAsync();var result=await assessment.Store(p,now);Audit("INSPECT",id);
            await db.SaveChangesAsync();await tx.CommitAsync();return Ok(new {row.Id,profileVersion=p.Version,assessment=Decision(result.Decision)});
        }catch(DbUpdateException){return Conflict(new {message="Không lưu được kiểm tra. Tải lại trước khi thử lại; yêu cầu không được tự gửi lại."});}
    }
    [HttpGet("{id:long}/services")]
    public async Task<IActionResult> Services(long id,[FromQuery]long? before=null) {
        if(before is <=0)return BadRequest();
        if(!await db.BatteryProfiles.AnyAsync(x=>x.Id==id))return NotFound();
        var items=await db.BatteryServices.AsNoTracking().Where(x=>x.ProfileId==id && x.DataSource=="REAL" && (!before.HasValue || x.Id<before))
            .OrderByDescending(x=>x.Id).Take(51).ToListAsync();
        return Ok(new {items=items.Take(50).Select(x=>new {x.Id,performedAtUtc=Utc(x.PerformedAtUtc),recordedAtUtc=Utc(x.RecordedAtUtc),
            x.Action,x.BatteryType,x.BatteryCount,x.RechargeCompleted,x.Outcome,x.ConfirmedCause,x.Note,x.PerformedBy}),
            nextBefore=items.Count>50?(long?)items[49].Id:null});
    }
    [HttpPost("{id:long}/services")]
    public async Task<IActionResult> Service(long id,BatteryServiceRequest r) {
        if(!await Actor())return Unauthorized();
        var now=DateTime.UtcNow;var time=r.PerformedAt?.UtcDateTime ?? now;
        if(time>now || time.Year<2000)return BadRequest(new {message="Thời điểm xử lý không hợp lệ hoặc ở tương lai."});
        if(r.RechargeCompleted.HasValue && r.Action!="RECHARGE")return BadRequest();
        await using var tx=await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var p=await Locked(id);if(p==null)return NotFound();
        if(!p.Enabled || p.Version!=r.Version || p.Version==int.MaxValue)return Conflict(new {message="Hồ sơ đã đổi hoặc ngừng theo dõi. Tải lại trước khi ghi."});
        if(r.Action=="RECHARGE" && p.Rechargeable==false)return BadRequest(new {message="Hồ sơ đang ghi loại pin không sạc. Kiểm tra và sửa hồ sơ trước."});
        var row=new EquipmentBatteryService {ProfileId=id,PerformedAtUtc=time,RecordedAtUtc=now,Action=r.Action,
            BatteryType=Clean(r.BatteryType),BatteryCount=r.BatteryCount,RechargeCompleted=r.RechargeCompleted,Outcome=r.Outcome,
            ConfirmedCause=r.ConfirmedCause,Note=Clean(r.Note),PerformedBy=User.FindFirstValue(ClaimTypes.NameIdentifier),DataSource="REAL"};
        db.BatteryServices.Add(row);p.Version++;p.UpdatedAtUtc=now;
        try {
            await db.SaveChangesAsync();var result=await assessment.Store(p,now);Audit("SERVICE",id);
            await db.SaveChangesAsync();await tx.CommitAsync();return Ok(new {row.Id,profileVersion=p.Version,assessment=Decision(result.Decision)});
        }catch(DbUpdateException){return Conflict(new {message="Không lưu được xử lý. Tải lại trước khi thử lại; yêu cầu không được tự gửi lại."});}
    }
    [HttpPost("{id:long}/assess")]
    public async Task<IActionResult> Assess(long id) {
        if(!await Actor())return Unauthorized();
        await using var tx=await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var p=await Locked(id);if(p==null)return NotFound();
        var value=await assessment.Store(p,DateTime.UtcNow);
        await db.SaveChangesAsync();await tx.CommitAsync();return Ok(Decision(value.Decision));
    }
}
