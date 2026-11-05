using System.Text.Json;
namespace ClassHub_API.Services;
public static class IotRoomUsage {
 public static string Resolve(string source,string? json,bool activeBorrowing,bool hasReturned) {
    // Real borrowing always takes priority over any context sent by a test node.
    if(activeBorrowing)return "IN_USE";
    if(source=="MOCK"){
        try{
            var readings=JsonSerializer.Deserialize<List<SensorReading>>(json??"[]",NodeTelemetry.Json);
            if(readings?.FirstOrDefault(r=>r.Metric=="current")?.UsageContext=="IN_USE")return "IN_USE";
        }catch(JsonException){}
        return "AFTER_USE"; // Default for this demo, without creating a borrowing/return record.
    }
    return hasReturned?"AFTER_USE":"UNKNOWN";
 }
}
