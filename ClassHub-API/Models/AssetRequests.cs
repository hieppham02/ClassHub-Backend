using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
namespace ClassHub_API.Models;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public class AssetReceiptRequest
{
    public int? CatalogId { get; set; }
    [Required, MaxLength(100)] public string Name { get; set; } = "";
    [Required, MaxLength(50)] public string Category { get; set; } = "Khác";
    [Range(1,10000)] public int Quantity { get; set; }
    [Required] public string TrackingMode { get; set; } = "BULK";
    [MaxLength(80)] public string? AssetCode { get; set; }
    [MaxLength(255)] public string? Note { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public class AssetConditionRequest
{
    [Range(1,10000)] public int AffectedCount { get; set; }
    [Range(1,int.MaxValue)] public int Version { get; set; }
    [Required] public string Condition { get; set; } = "TOT";
    [Required, MaxLength(255)] public string Note { get; set; } = "";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public class AssetMoveRequest
{
    [Required] public string Action { get; set; } = "ALLOCATE";
    [Range(1,10000)] public int Quantity { get; set; }
    [Range(1,int.MaxValue)] public int Version { get; set; }
    [MaxLength(50)] public string? TargetRoomId { get; set; }
    [Required, MaxLength(255)] public string Note { get; set; } = "";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public class AssetCountRequest
{
    [Range(0,10000)] public int ActualCount { get; set; }
    [Range(1,int.MaxValue)] public int Version { get; set; }
    [Required, MaxLength(255)] public string Note { get; set; } = "";
}

public class NodeStatusRequest { [Required] public string Status { get; set; } = "ACTIVE"; }
