using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
namespace ClassHub_API.Models;
[Table("equipment_catalog")]
public class EquipmentCatalog
{
    [Key, Column("id")] public int Id { get; set; }
    [Column("name"), MaxLength(100)] public string Name { get; set; } = "";
    [Column("category"), MaxLength(50)] public string Category { get; set; } = "Khác";
    [Column("tracking_mode"), MaxLength(20)] public string TrackingMode { get; set; } = "BULK";
    [Column("usage_scope"), MaxLength(20)] public string UsageScope { get; set; } = "ROOM";
    [Column("enabled")] public bool Enabled { get; set; } = true;
    [Column("version")] public int Version { get; set; } = 1;
    [Column("code"), MaxLength(36)] public string Code { get; set; } = Guid.NewGuid().ToString();
}

public class CatalogRequest
{
    [Required, MaxLength(100)] public string Name { get; set; } = "";
    [Required, MaxLength(50)] public string Category { get; set; } = "Khác";
    [Required] public string TrackingMode { get; set; } = "BULK";
    public bool Enabled { get; set; } = true;
    [Range(1,int.MaxValue)] public int Version { get; set; } = 1;
}
public class RoomEquipmentRequest
{
    [Range(1,int.MaxValue)] public int EquipmentId { get; set; }
    [Range(1,10000)] public int Quantity { get; set; }
    [Range(1,int.MaxValue)] public int Version { get; set; }
    [Required, MaxLength(255)] public string Note { get; set; } = "";
}
public class RoomQuantityRequest
{
    [Range(0,10000)] public int ActualCount { get; set; }
    [Range(1,int.MaxValue)] public int Version { get; set; }
    [Required, MaxLength(255)] public string Note { get; set; } = "";
}
