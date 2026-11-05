using ClassHub_API.Data;
using ClassHub_API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
namespace ClassHub_API.Controllers;

[ApiController,Authorize(Roles="ADMIN"),Route("api/admin/assets/nodes")]
public class IotAnalysisController(AppDbContext db,IotAnomalyModel model):ControllerBase {
 [HttpGet("{id:int}/analysis")]
 public async Task<IActionResult> Analysis(int id) {
    var node=await db.IotNodes.AsNoTracking().FirstOrDefaultAsync(n=>n.Id==id);
    if(node==null)return NotFound();
    var sample=await db.IotSensors.AsNoTracking().Where(s=>s.NodeId==id && s.DataSource==node.DataSource)
        .OrderByDescending(s=>s.ReceivedAt).ThenByDescending(s=>s.Id).FirstOrDefaultAsync();
    object Pending(string reason)=>new{state="UNAVAILABLE",reason,source=node.DataSource,modelVersion=model.Version};
    var now=DateTime.UtcNow;
    var gap=Math.Max(90,node.PublishIntervalSeconds*3);
    if(node.Status=="DISABLED" || sample==null || !sample.ClockSynced || sample.MeasuredAt==null ||
       sample.MeasuredAt<now.AddSeconds(-gap) || sample.MeasuredAt>now.AddSeconds(30) ||
       sample.ReceivedAt<now.AddSeconds(-gap))
        return Ok(Pending("Chưa có số đo mới và đồng bộ thời gian để phân tích."));
    List<SensorReading>? rows;
    try{rows=JsonSerializer.Deserialize<List<SensorReading>>(sample.ReadingsJson,NodeTelemetry.Json);}
    catch(JsonException){return Ok(Pending("Dữ liệu cảm biến không hợp lệ."));}
    double? Value(string metric)=>rows?.FirstOrDefault(r=>r.Metric==metric && r.Quality=="GOOD")?.Value;
    var temperature=Value("temperature");var humidity=Value("humidity");var current=Value("current");
    if(temperature==null || humidity==null || current==null ||
       !double.IsFinite(temperature.Value) || !double.IsFinite(humidity.Value) || !double.IsFinite(current.Value))
        return Ok(Pending("Cần đủ nhiệt độ, độ ẩm và dòng điện có chất lượng GOOD."));
    var active=await db.phieu_muon.AnyAsync(s=>s.ma_phong==node.RoomId && s.thoi_gian_nhan!=null &&
        new[]{"ACTIVE","IN_USE","RETURNING"}.Contains(s.trang_thai));
    var returned=await db.phieu_muon.AnyAsync(s=>s.ma_phong==node.RoomId && s.trang_thai=="COMPLETED" && s.thoi_gian_tra!=null);
    var usage=IotRoomUsage.Resolve(node.DataSource,sample.ReadingsJson,active,returned);
    var basis=node.DataSource=="MOCK"?"Ngữ cảnh từ bộ phát MQTT; mặc định đã trả. Phiên mượn thực tế được ưu tiên.":"Phiên mượn / trả trên ClassHub";
    bool known=usage!="UNKNOWN";
    bool afterReturn=usage=="AFTER_USE";
    var probabilities=model.Predict(temperature.Value,humidity.Value,current.Value,!known || usage=="IN_USE");
    var power=probabilities[1]+probabilities[3];var heat=probabilities[2]+probabilities[3];
    var findings=new List<object>();
    if(afterReturn && power>=.6) findings.Add(new{code="POWER_AFTER_USE",severity="warning",
        title="Có dấu hiệu còn thiết bị sử dụng điện",detail="Dòng điện còn cao khi phòng nghỉ hoặc sau sử dụng. Kiểm tra đèn, TV, máy chiếu và điều hòa; chưa xác định được thiết bị cụ thể.",score=Math.Round(power*100,1)});
    if(afterReturn && (heat>=.6 || temperature>=35)) findings.Add(new{code="HIGH_TEMPERATURE",severity="critical",
        title="Nhiệt độ cao bất thường",detail="Kiểm tra thông gió, điều hòa và nguồn nhiệt trong phòng.",score=Math.Round(heat*100,1)});
    if(afterReturn && humidity>=75) findings.Add(new{code="HIGH_HUMIDITY",severity="warning",
        title="Độ ẩm cao sau trả đồ",detail="Kiểm tra độ ẩm và thông gió trong phòng.",score=Math.Round(humidity.Value,1),scoreLabel="Độ ẩm",scoreUnit="%"});
    return Ok(new{state=usage=="IN_USE"?"IN_USE":findings.Count>0?"ANOMALY":"NORMAL",source=node.DataSource,modelVersion=model.Version,
        algorithm="Random Forest",trainingSource="SYNTHETIC",usageContext=usage,contextBasis=basis,contextKnown=known,
        sampleId=sample.Id,measuredAt=DateTime.SpecifyKind(sample.MeasuredAt.Value,DateTimeKind.Utc),
        temperatureC=temperature,humidityPercent=humidity,currentAmperes=current,findings,
        summary=usage=="IN_USE"?"Phòng đang hoạt động":findings.Count>0?"Cần kiểm tra phòng":known?"Chưa phát hiện bất thường":"Chưa có phiên trả để đánh giá cảnh báo",
        limitation="Mô hình học từ dữ liệu tổng hợp, chưa kiểm chứng thực địa. Điểm mô hình không phải xác suất sự cố thực tế."});
 }
}
