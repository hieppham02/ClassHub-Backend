using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ClassHub_API.Models;

// One row is one telemetry message containing multiple readings.
[Table("iot_sensor")]
public class IotSensor
{
    [Key, Column("id")] public long Id { get; set; }
    [Column("node_id")] public int NodeId { get; set; }
    [Column("boot_id"), MaxLength(36)] public string BootId { get; set; } = null!;
    [Column("sequence")] public ulong Sequence { get; set; }
    [Column("measured_at", TypeName = "datetime(3)")] public DateTime? MeasuredAt { get; set; }
    [Column("received_at", TypeName = "datetime(3)")] public DateTime ReceivedAt { get; set; }
    [Column("clock_synced")] public bool ClockSynced { get; set; }
    [Column("data_source"), MaxLength(10)] public string DataSource { get; set; } = null!;
    [Column("readings_json", TypeName = "longtext")] public string ReadingsJson { get; set; } = null!;
    [ForeignKey(nameof(NodeId))] public virtual IotNode Node { get; set; } = null!;
}
