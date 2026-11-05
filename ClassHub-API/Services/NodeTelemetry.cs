using ClassHub_API.Data;
using ClassHub_API.Models;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace ClassHub_API.Services;
public record SensorReading(string Metric, string Unit, double? Value, string Quality);
public class NodeMessage {
 public int SchemaVersion {get;set;}
 public string? Source {get;set;}
 public string? NodeCode {get;set;}
 public string? RoomId {get;set;}
 public string? BootId {get;set;}
 public long BootSequence {get;set;}
 public long Sequence {get;set;}
 public bool ClockSynced {get;set;}
 public DateTime? MeasuredAtUtc {get;set;}
 public List<SensorReading>? Readings {get;set;}
}
public static class NodeTelemetry {
 public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy=JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive=true };
 public static bool SourceMatches(string expected,string? actual) => actual is ("REAL" or "MOCK") && expected==actual;
 public static bool Valid(NodeMessage p, DateTime now) => p.SchemaVersion==1 && p.Source is ("REAL" or "MOCK") && p.NodeCode!=null
    && Regex.IsMatch(p.NodeCode,@"\A[A-Za-z0-9_-]{1,50}\z") && Guid.TryParseExact(p.BootId,"D",out _)
    && p.BootSequence is >0 and <=uint.MaxValue && p.Sequence is >0 and <=uint.MaxValue
    && (!p.ClockSynced || p.MeasuredAtUtc?.Kind==DateTimeKind.Utc && p.MeasuredAtUtc>=now.AddMinutes(-5) && p.MeasuredAtUtc<=now.AddSeconds(30))
    && p.Readings?.Count is >=1 and <=16 && p.Readings.Select(r=>r?.Metric).Distinct().Count()==p.Readings.Count
    && p.Readings.All(r=>r!=null && r.Quality is "GOOD" or "ERROR" or "UNCALIBRATED" && ReadingValid(r));
 static bool ReadingValid(SensorReading r) {
    var range=r.Metric switch { "current" when r.Unit=="A" => (0d,1000d), "temperature" when r.Unit=="C" => (-50d,150d),
        "humidity" when r.Unit=="%" => (0d,100d), "co2" when r.Unit=="ppm" => (0d,100000d), "light" when r.Unit=="lux" => (0d,200000d), _ => (double.NaN,double.NaN) };
    return !double.IsNaN(range.Item1) && (r.Value.HasValue ? double.IsFinite(r.Value.Value) && r.Value>=range.Item1 && r.Value<=range.Item2 : r.Quality!="GOOD");
 }
 // Broker ACL must bind each device identity to its exact topic; source labels are not credentials.
 public static async Task<bool> Accept(AppDbContext db,string topic,string body,DateTime now) {
    if(body.Length>8192) return false;
    NodeMessage? p;
    try { p=JsonSerializer.Deserialize<NodeMessage>(body,Json); } catch(JsonException){return false;}
    if(p==null || !Valid(p,now) || topic!=$"iot/node/{p.NodeCode}/telemetry") return false;
    await using var tx=await db.Database.BeginTransactionAsync();
    var node=await db.IotNodes.FromSqlInterpolated($"SELECT * FROM iot_node WHERE node_code={p.NodeCode} FOR UPDATE").FirstOrDefaultAsync();
    if(node==null || !SourceMatches(node.DataSource,p.Source) || node.Status=="DISABLED" || node.RoomId!=p.RoomId || p.BootSequence<node.BootSequence
        || p.BootSequence==node.BootSequence && (p.BootId!=node.LastBootId || p.Sequence<=node.LastSequence)) return false;
    db.IotSensors.Add(new IotSensor {NodeId=node.Id,BootId=p.BootId!,Sequence=(ulong)p.Sequence,ReceivedAt=now,
        MeasuredAt=p.ClockSynced?p.MeasuredAtUtc:null, ClockSynced=p.ClockSynced,DataSource=p.Source!,ReadingsJson=JsonSerializer.Serialize(p.Readings,Json)});
    node.BootSequence=p.BootSequence;node.LastBootId=p.BootId;node.LastSequence=p.Sequence;node.LastSeenAt=now;node.Status="ONLINE";
    await db.SaveChangesAsync();await tx.CommitAsync();return true;
 }
 public static double? Current(IotSensor sample) {
    if(sample.DataSource!="REAL" || !sample.ClockSynced || sample.MeasuredAt==null) return null;
    try { return JsonSerializer.Deserialize<List<SensorReading>>(sample.ReadingsJson,Json)?.FirstOrDefault(r=>r.Metric=="current"&&r.Unit=="A"&&r.Quality=="GOOD"&&r.Value>=0)?.Value; }
    catch(JsonException){return null;}
 }
}
