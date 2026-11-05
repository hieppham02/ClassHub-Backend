using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
namespace ClassHub_API.Models;

[Table("cabinet_health")]
public class CabinetHealth
{
    [Key, Column("room_id"), MaxLength(50)] public string RoomId { get; set; } = "";
    [Column("received_at_utc")] public DateTime ReceivedAtUtc { get; set; }
    [Column("boot_sequence")] public long BootSequence { get; set; }
    [Column("boot_id"), MaxLength(32)] public string BootId { get; set; } = "";
    [Column("uptime_ms")] public long UptimeMs { get; set; }
    [Column("message_sequence")] public long MessageSequence { get; set; }
    [Column("payload_json", TypeName = "longtext")] public string PayloadJson { get; set; } = "{}";
}
