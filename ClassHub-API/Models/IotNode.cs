using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ClassHub_API.Models;

[Table("iot_node")]
public class IotNode
{
    [Key, Column("id")] public int Id { get; set; }
    [Column("node_code"), MaxLength(50)] public string NodeCode { get; set; } = null!;
    [Column("room_id"), MaxLength(50)] public string RoomId { get; set; } = null!;
    [Column("name"), MaxLength(100)] public string Name { get; set; } = null!;
    [Column("data_source"), MaxLength(10)] public string DataSource { get; set; } = "MOCK";
    [Column("status"), MaxLength(10)] public string Status { get; set; } = "OFFLINE";
    [Column("last_seen_at", TypeName = "datetime(3)")] public DateTime? LastSeenAt { get; set; }
    [Column("publish_interval_seconds")] public int PublishIntervalSeconds { get; set; } = 30;
    [Column("boot_sequence")] public long BootSequence { get; set; }
    [Column("last_boot_id")] public string? LastBootId { get; set; }
    [Column("last_sequence")] public long LastSequence { get; set; }
    [ForeignKey(nameof(RoomId))] public virtual PhongHoc Room { get; set; } = null!;
}
