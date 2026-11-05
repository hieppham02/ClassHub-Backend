using ClassHub_API.Data;
using ClassHub_API.Models;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
namespace ClassHub_API.Services;
public record CurrentAssessment(string State, double? CurrentAmperes, bool IsFresh, bool RoomHasActiveBorrowing,
    DateTime? MeasuredAt, double? ThresholdAmperes, DateTime? SinceUtc, long? EvidenceSampleId);
public static class OperationRules {
 public static async Task<CurrentAssessment> Current(AppDbContext db,IConfiguration config,IotNode node,DateTime now) {
    if(node.DataSource!="REAL") return new("MOCK",null,false,false,null,null,null,null);
    var local=now.AddHours(7); // Existing borrowing timestamps are Vietnam local wall time.
    var active=await db.phieu_muon.AnyAsync(s=>s.ma_phong==node.RoomId && s.thoi_gian_nhan!=null
        && new[]{"ACTIVE","IN_USE","RETURNING"}.Contains(s.trang_thai));
    var threshold=config.GetValue<double?>($"Operations:CurrentThresholdAmperes:{node.NodeCode}");
    var duration=config.GetValue<int?>("Operations:CurrentDurationSeconds")??300;
    if(duration<60 || duration>3600) duration=300;
    var lastReturn=await db.phieu_muon.Where(s=>s.ma_phong==node.RoomId && s.trang_thai=="COMPLETED" && s.thoi_gian_tra!=null
        && s.thoi_gian_tra<=local && s.thoi_gian_tra>=local.AddDays(-1)).MaxAsync(s=>s.thoi_gian_tra);
    var cutoff=now.AddSeconds(-duration-3600);
    var samples=await db.IotSensors.AsNoTracking().Where(s=>s.NodeId==node.Id && s.DataSource=="REAL" && s.ReceivedAt>=cutoff && s.ReceivedAt<=now)
        .OrderByDescending(s=>s.ReceivedAt).ThenByDescending(s=>s.Id).Take(2000).ToListAsync();
    var latest=samples.FirstOrDefault();var amperes=latest==null?null:NodeTelemetry.Current(latest);
    var gap=Math.Clamp((long)node.PublishIntervalSeconds*3,90,3600);
    bool fresh=latest!=null && (now-latest.ReceivedAt).TotalSeconds<=gap && latest.MeasuredAt<=now.AddSeconds(30)
        && latest.MeasuredAt>=now.AddSeconds(-gap) && amperes.HasValue;
    string state=!fresh?"NO_DATA":threshold is not >0 or >1000?"NOT_CONFIGURED":active?"IN_USE":amperes<threshold?"LOW":!lastReturn.HasValue?"NO_RETURN_CONTEXT":"OBSERVING";
    DateTime? since=null;
    if(state=="OBSERVING") {
        var previous=now;var boot=latest!.BootId;
        foreach(var s in samples) {
            var current=NodeTelemetry.Current(s);
            if(s.BootId!=boot || (previous-s.ReceivedAt).TotalSeconds>gap || s.MeasuredAt<lastReturn!.Value.AddHours(-7)
                || s.MeasuredAt<s.ReceivedAt.AddSeconds(-gap) || s.MeasuredAt>s.ReceivedAt.AddSeconds(30)
                || current==null || current<threshold) break;
            since=s.ReceivedAt;previous=s.ReceivedAt;
        }
        if(since.HasValue && (latest.ReceivedAt-since.Value).TotalSeconds>=duration) state="AFTER_RETURN_CURRENT";
    }
    return new(state,amperes,fresh,active,latest?.MeasuredAt is DateTime measured?DateTime.SpecifyKind(measured,DateTimeKind.Utc):null,threshold,since.HasValue?DateTime.SpecifyKind(since.Value,DateTimeKind.Utc):null,latest?.Id);
 }
 public static async Task SetAlert(AppDbContext db,string key,string room,string rule,bool? active,object evidence,DateTime now) {
    if(active==null) return; // Unknown does not claim an earlier fault was repaired.
    var row=await db.OperationAlerts.FirstOrDefaultAsync(a=>a.AlertKey==key && a.Status=="OPEN");
    if(active==true && row==null) {row=new(){AlertKey=key,RoomId=room,Rule=rule,StartedAtUtc=now};db.OperationAlerts.Add(row);}
    if(row==null)return;
    row.LastEvaluatedAtUtc=now;row.EvidenceJson=JsonSerializer.Serialize(evidence);
    if(active==false){row.Status="RESOLVED";row.ResolvedAtUtc=now;}
 }
 public static async Task Evaluate(AppDbContext db,IConfiguration config,DateTime now) {
    int doorSeconds=Math.Clamp(config.GetValue<int?>("Operations:DoorOpenSeconds")??120,30,3600);
    var health=await db.CabinetHealths.AsNoTracking().ToListAsync();
    foreach(var h in health) {
        var latest=await db.OperationSamples.AsNoTracking().Where(s=>s.RoomId==h.RoomId).OrderByDescending(s=>s.Id).FirstOrDefaultAsync();
        bool fresh=(now-h.ReceivedAtUtc).TotalSeconds<=45;
        await SetAlert(db,"cabinet-missing:"+h.RoomId,h.RoomId,"CABINET_MESSAGES_MISSING",!fresh,new{h.ReceivedAtUtc,expectedMaxGapSeconds=45},now);
        bool? door=fresh && latest?.DoorOpen!=null ? latest.DoorOpen==true && latest.DoorOpenSinceUtc.HasValue
            && (now-latest.DoorOpenSinceUtc.Value).TotalSeconds>=doorSeconds : null;
        // An open door still under threshold cannot resolve an existing long-open incident after a gap/reboot.
        if(latest?.DoorOpen==true && door==false) door=null;
        await SetAlert(db,"door:"+h.RoomId,h.RoomId,"DOOR_OPEN_LONG",door,new{sampleId=latest?.Id,latest?.DoorOpenSinceUtc,thresholdSeconds=doorSeconds},now);
    }
    var nodes=await db.IotNodes.AsNoTracking().Where(n=>n.DataSource=="REAL" && n.Status!="DISABLED").ToListAsync();
    foreach(var node in nodes) {
        if(node.LastSeenAt.HasValue) await SetAlert(db,"node-missing:"+node.Id,node.RoomId,"NODE_MESSAGES_MISSING",
            (now-node.LastSeenAt.Value).TotalSeconds>Math.Clamp((long)node.PublishIntervalSeconds*3,90,3600),new{nodeId=node.Id,node.LastSeenAt},now);
        var power=await Current(db,config,node,now);
        bool? fault=power.State=="AFTER_RETURN_CURRENT"?true:power.State is "LOW" or "IN_USE"?false:null;
        await SetAlert(db,"after-return:"+node.Id,node.RoomId,"AFTER_RETURN_CURRENT",fault,new{nodeId=node.Id,assessment=power,scope="ROOM_TOTAL",ruleVersion=1},now);
    }
    await db.SaveChangesAsync();
 }
}
public class OperationRulesWorker(IServiceScopeFactory scopes,IConfiguration config,ILogger<OperationRulesWorker> logger):BackgroundService {
 protected override async Task ExecuteAsync(CancellationToken stop) {
    using var timer=new PeriodicTimer(TimeSpan.FromSeconds(15));
    try { while(await timer.WaitForNextTickAsync(stop)) {
        try {using var scope=scopes.CreateScope();var db=scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await OperationRules.Evaluate(db,config,DateTime.UtcNow);
        }catch(Exception ex){logger.LogError(ex,"Operation rule evaluation failed");}
    }}catch(OperationCanceledException)when(stop.IsCancellationRequested){}
 }
}
