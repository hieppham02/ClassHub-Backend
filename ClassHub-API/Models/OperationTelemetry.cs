using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
namespace ClassHub_API.Models;
[Table("operation_sample")]
public class OperationSample {
 [Key, Column("id")] public long Id {get;set;}
 [Column("room_id")] public string RoomId {get;set;} = "";
 [Column("received_at_utc")] public DateTime ReceivedAtUtc {get;set;}
 [Column("boot_sequence")] public long BootSequence {get;set;}
 [Column("message_sequence")] public long MessageSequence {get;set;}
 [Column("door_open")] public bool? DoorOpen {get;set;}
 [Column("door_open_since_utc")] public DateTime? DoorOpenSinceUtc {get;set;}
 [Column("payload_json")] public string PayloadJson {get;set;} = "{}";
}
[Table("operation_alert")]
public class OperationAlert {
 [Key, Column("id")] public long Id {get;set;}
 [Column("alert_key")] public string AlertKey {get;set;} = "";
 [Column("room_id")] public string RoomId {get;set;} = "";
 [Column("rule")] public string Rule {get;set;} = "";
 [Column("source")] public string Source {get;set;} = "REAL";
 [Column("status")] public string Status {get;set;} = "OPEN";
 [Column("started_at_utc")] public DateTime StartedAtUtc {get;set;}
 [Column("last_evaluated_at_utc")] public DateTime LastEvaluatedAtUtc {get;set;}
 [Column("resolved_at_utc")] public DateTime? ResolvedAtUtc {get;set;}
 [Column("evidence_json")] public string EvidenceJson {get;set;} = "{}";
}
