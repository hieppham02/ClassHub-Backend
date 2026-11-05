using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
namespace ClassHub_API.Models;

[Table("equipment_rfid_tag")]
public class EquipmentRfidTag
{
    [Key, Column("id")] public int Id { get; set; }
    [Column("equipment_id")] public int EquipmentId { get; set; }
    [Column("uid"), MaxLength(20)] public string Uid { get; set; } = "";
    [Column("created_at_utc")] public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
