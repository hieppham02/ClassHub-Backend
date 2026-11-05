using System.Text.Json;
namespace ClassHub_API.Services;
public static class IotAttention {
 public static bool Check(string? json,string source,IotAnomalyModel? model,string? roomUsage = null) {
    if(string.IsNullOrWhiteSpace(json))return false;
    List<SensorReading>? rows;
    try {rows=JsonSerializer.Deserialize<List<SensorReading>>(json,NodeTelemetry.Json);}
    catch(JsonException){return false;}
    double? Value(string key)=>rows?.FirstOrDefault(r=>r.Metric==key && r.Quality=="GOOD" && r.Value.HasValue && double.IsFinite(r.Value.Value))?.Value;
    var usage=roomUsage ?? IotRoomUsage.Resolve(source,json,false,false);
    if(usage!="AFTER_USE")return false;
    var t=Value("temperature");var h=Value("humidity");var a=Value("current");
    if(t>=35 || h>=75)return true;
    if(model==null || t==null || h==null || a==null)return false;
    var probabilities=model.Predict(t.Value,h.Value,a.Value,false);
    return probabilities[1]+probabilities[3]>=.6;
 }
}
