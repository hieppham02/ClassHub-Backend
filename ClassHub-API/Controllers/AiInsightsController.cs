using ClassHub_API.Data;
using ClassHub_API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
namespace ClassHub_API.Controllers;
[ApiController,Authorize(Roles="ADMIN"),Route("api/admin/ai")]
public class AiInsightsController(AppDbContext db,BatteryForecastModel model):ControllerBase {
    static readonly TimeZoneInfo Vietnam=TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");
    static DateTime LocalNow=>TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow,Vietnam);
    [HttpGet("model")]
    public IActionResult Model() => Ok(new {modelVersion=model.Version,metadata=model.Metadata,examples=model.GoldenVectors});
    [HttpPost("battery/predict")]
    public IActionResult Predict(BatteryForecastInput input) {
        if(!input.Valid)return BadRequest(new {message="Kiểm tra tuổi pin, thời gian mượn, số lượt và số ngày sử dụng. Giờ mượn không vượt 24 giờ/ngày; 7 ngày gần nhất không vượt tổng thời gian."});
        return Ok(model.Predict(input,DateTime.UtcNow));
    }
    [HttpGet("battery/forecasts")]
    public async Task<IActionResult> Forecasts(CancellationToken ct) {
        var profiles=await (from p in db.BatteryProfiles.AsNoTracking() join e in db.thiet_bi.AsNoTracking() on p.EquipmentId equals e.id
            where p.Enabled orderby p.Id select new {p.Id,p.EquipmentId,p.AssetCode,p.Rechargeable,p.CurrentRfidTagId,name=e.ten_thiet_bi,room=e.ma_phong,e.so_luong}).Take(201).ToListAsync(ct);
        if(profiles.Count==0)return Ok(new {asOfUtc=DateTime.UtcNow,modelVersion=model.Version,trainingSource="SYNTHETIC",items=Array.Empty<object>()});
        if(profiles.Count>200)return BadRequest(new {message="Chỉ hỗ trợ tối đa 200 hồ sơ trong một lần dự báo."});
        var ids=profiles.Select(p=>p.Id).ToArray();var equipmentIds=profiles.Select(p=>p.EquipmentId).Distinct().ToArray();
        var cycles=await db.BatteryServices.AsNoTracking().Where(s=>ids.Contains(s.ProfileId) && s.DataSource=="REAL" &&
            s.PerformedAtUtc<=DateTime.UtcNow && s.Outcome=="NORMAL" && (s.Action=="REPLACE_BATTERY" || s.Action=="RECHARGE" && s.RechargeCompleted==true)).ToListAsync(ct);
        var sessions=await (from b in db.phieu_muon_thiet_bi.AsNoTracking() join s in db.phieu_muon.AsNoTracking() on b.phieu_muon_id equals s.id
            where b.thiet_bi_id!=null && equipmentIds.Contains(b.thiet_bi_id.Value) && s.thoi_gian_nhan!=null &&
                (s.trang_thai=="COMPLETED" || s.trang_thai=="ACTIVE" || s.trang_thai=="IN_USE" || s.trang_thai=="RETURNING")
            select new {equipmentId=b.thiet_bi_id!.Value,s.id,s.thoi_gian_nhan,s.thoi_gian_tra,s.trang_thai}).Take(20001).ToListAsync(ct);
        if(sessions.Count>20000)return BadRequest(new {message="Lịch sử quá lớn; cần thu hẹp phạm vi dự báo."});
        var tagIds=profiles.Where(p=>p.CurrentRfidTagId.HasValue).Select(p=>p.CurrentRfidTagId!.Value).Distinct().ToArray();
        var tags=await db.EquipmentRfidTags.AsNoTracking().Where(t=>tagIds.Contains(t.Id)).ToListAsync(ct);
        var sessionIds=sessions.Select(s=>s.id).Distinct().ToArray();
        var qr=await db.QrAccesses.AsNoTracking().Where(q=>sessionIds.Contains(q.SessionId) && q.Purpose=="RECEIVE" && q.RfidObservedJson!=null)
            .OrderByDescending(q=>q.Id).Select(q=>new {q.SessionId,q.RfidObservedJson}).ToListAsync(ct);
        var now=DateTime.UtcNow;var local=LocalNow;var result=new List<object>();
        foreach(var p in profiles) {
            var cycle=cycles.Where(c=>c.ProfileId==p.Id).OrderByDescending(c=>c.PerformedAtUtc).FirstOrDefault();
            if(cycle==null){result.Add(new {p.Id,p.AssetCode,p.name,p.room,state="MISSING_CYCLE",message="Chưa ghi nhận lần thay/sạc pin hoàn tất."});continue;}
            var lower=p.name.ToLowerInvariant();var kind=lower.Contains("mic") || lower.Contains("micro")?0:
                lower.Contains("remote") || lower.Contains("điều khiển")?1:-1;
            if(kind<0){result.Add(new {p.Id,p.AssetCode,p.name,p.room,state="UNSUPPORTED_DEVICE",message="Mô hình hỗ trợ mic và điều khiển."});continue;}
            var cycleUtc=DateTime.SpecifyKind(cycle.PerformedAtUtc,DateTimeKind.Utc);
            var age=(now-cycleUtc).TotalDays;
            if(age>730){result.Add(new {p.Id,p.AssetCode,p.name,p.room,state="OUTSIDE_RANGE",message="Chu kỳ vượt 730 ngày. Cần kiểm tra trực tiếp."});continue;}
            var from=TimeZoneInfo.ConvertTimeFromUtc(cycleUtc,Vietnam);
            var uid=tags.FirstOrDefault(t=>t.Id==p.CurrentRfidTagId)?.Uid;
            if(uid==null && p.so_luong>1){result.Add(new {p.Id,p.AssetCode,p.name,p.room,state="MISSING_RFID_LINK",message="Nhóm có nhiều thiết bị: cần gắn RFID vào hồ sơ để tính riêng từng chiếc."});continue;}
            var relevant=sessions.Where(s=>s.equipmentId==p.EquipmentId && (uid==null ||
                (RfidSession.ParseObserved(qr.FirstOrDefault(q=>q.SessionId==s.id)?.RfidObservedJson)??[]).Contains(uid)));
            var ranges=relevant.Select(s=>new {s.id,start=s.thoi_gian_nhan!.Value,end=s.thoi_gian_tra??(s.trang_thai=="COMPLETED"?s.thoi_gian_nhan.Value:local)})
                .Where(s=>s.end>from && s.start<local && s.end>s.start).ToList();
            var intervals=ranges.Select(s=>new UsageInterval(s.start<from?from:s.start,s.end>local?local:s.end)).ToList();
            var hours=UsageIntervals.Hours(intervals);var recent=UsageIntervals.Overlap(intervals,[new(local.AddDays(-7),local)]);
            var active=Enumerable.Range(0,7).Count(i=>UsageIntervals.Overlap(intervals,[new(local.AddDays(-i-1),local.AddDays(-i))])>0);
            var input=new BatteryForecastInput(kind,age,hours,ranges.Select(s=>s.id).Distinct().Count(),recent,active,p.Rechargeable??false);
            if(!input.Valid){result.Add(new {p.Id,p.AssetCode,p.name,p.room,state="INVALID_HISTORY",message="Dữ liệu thời gian không hợp lệ, cần kiểm tra."});continue;}
            result.Add(new {p.Id,p.AssetCode,p.name,p.room,state="READY",input,forecast=model.Predict(input,now),
                usageSource=uid==null?"EQUIPMENT_SESSION_PROXY":"RFID_CONFIRMED_SESSION_PROXY",cycleAtUtc=cycleUtc});
        }
        return Ok(new {asOfUtc=now,modelVersion=model.Version,trainingSource="SYNTHETIC",items=result});
    }
    [HttpGet("rooms")]
    public async Task<IActionResult> Rooms([FromQuery]DateTime? from,[FromQuery]DateTime? to,CancellationToken ct) {
        var today=LocalNow.Date;var start=(from??today.AddDays(-13)).Date;var end=(to??today).Date;
        if(end<start || (end-start).TotalDays>89 || end>today)return BadRequest(new {message="Chọn khoảng từ 1 đến 90 ngày, không vượt ngày hiện tại."});
        var limit=end.AddDays(1);
        var rooms=await db.phong_hoc.AsNoTracking().Where(r=>r.LocationType=="ROOM").Select(r=>new {id=r.ma_phong,name=r.ten_phong}).ToListAsync(ct);
        var slotRows=await db.cau_hinh_ca_hoc.AsNoTracking().OrderBy(s=>s.ca_id).Take(5).ToListAsync(ct);
        var slots=slotRows.Select(s=>new UsageSlot(s.ca_id,s.ten_ca,s.gio_bat_dau,s.gio_ket_thuc)).ToArray();
        if(slots.Length!=5)return Conflict(new {message="Cần cấu hình đúng 5 ca học để thống kê."});
        var roomIds=rooms.Select(r=>r.id).ToArray();
        var rows=await db.phieu_muon.AsNoTracking().Where(s=>roomIds.Contains(s.ma_phong) && s.thoi_gian_nhan!=null &&
            s.thoi_gian_nhan<limit && (s.thoi_gian_tra>=start || s.thoi_gian_tra==null) &&
            (s.trang_thai=="COMPLETED" || s.trang_thai=="ACTIVE" || s.trang_thai=="IN_USE" || s.trang_thai=="RETURNING"))
            .Select(s=>new {s.id,s.ma_phong,s.thoi_gian_nhan,s.thoi_gian_tra,s.trang_thai}).Take(20001).ToListAsync(ct);
        if(rows.Count>20000)return BadRequest(new {message="Quá nhiều phiên; chọn khoảng ngày ngắn hơn."});
        var now=LocalNow;
        var sessions=rows.Select(s=>new UsageSession(s.id,s.ma_phong,s.thoi_gian_nhan!.Value,
            s.thoi_gian_tra??(s.trang_thai=="COMPLETED"?s.thoi_gian_nhan.Value:now)))
            .Where(s=>s.End>s.Start && s.Start<now).ToList();
        var days=(int)(end-start).TotalDays+1;var result=new List<object>();
        foreach(var room in rooms) {
            var own=sessions.Where(s=>s.RoomId==room.id).ToList();
            var intervals=own.Select(s=>new UsageInterval(s.Start,s.End>now?now:s.End)).ToList();
            var daily=new List<object>();int used=0;double hours=0,capacity=0;
            foreach(var offset in Enumerable.Range(0,days)) {
                var day=start.AddDays(offset);var windows=UsageIntervals.Windows(day,slots);
                var n=UsageIntervals.UsedSlots(intervals,windows);var h=UsageIntervals.Overlap(intervals,windows);var c=UsageIntervals.Hours(windows);
                used+=n;hours+=h;capacity+=c;
                daily.Add(new {date=day.ToString("yyyy-MM-dd"),usedSlots=n,capacitySlots=5,
                    borrowCount=own.Count(s=>s.Start>=day && s.Start<day.AddDays(1)),usageHours=Math.Round(h,2),
                    slots=slots.Select((s,i)=>new {s.Id,s.Name,used=UsageIntervals.Overlap(intervals,[windows[i]])>0}).ToArray()});
            }
            result.Add(new {roomId=room.id,roomName=room.name,borrowCount=own.Count(s=>s.Start>=start && s.Start<limit),
                usedSlots=used,capacitySlots=days*5,slotUtilizationPercent=Math.Round(100d*used/(days*5),1),
                usageHours=Math.Round(hours,2),capacityHours=Math.Round(capacity,2),timeUtilizationPercent=capacity>0?Math.Round(100*hours/capacity,1):0,daily});
        }
        return Ok(new {from=start.ToString("yyyy-MM-dd"),to=end.ToString("yyyy-MM-dd"),days,timeZone="Asia/Ho_Chi_Minh",
            method="Observed receive/return intervals, merged overlaps; not proof of physical occupancy.",roomCount=rooms.Count,items=result});
    }
}
