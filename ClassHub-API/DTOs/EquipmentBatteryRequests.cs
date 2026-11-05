using System.ComponentModel.DataAnnotations;
namespace ClassHub_API.DTOs;
public class BatteryProfileRequest {
    [Range(1,int.MaxValue)] public int EquipmentId { get; set; }
    [Required, StringLength(80), RegularExpression(@"[A-Za-z0-9_-]{1,80}")] public string AssetCode { get; set; } = "";
    [Range(1,int.MaxValue)] public int? CurrentRfidTagId { get; set; }
    [StringLength(80)] public string? BatteryType { get; set; }
    [Range(1,100)] public short? BatteryCount { get; set; }
    public bool? Rechargeable { get; set; }
    [StringLength(100)] public string? Manufacturer { get; set; }
    [StringLength(100)] public string? Model { get; set; }
    public bool Enabled { get; set; } = true;
    [Range(1,int.MaxValue)] public int Version { get; set; } = 1;
}
public class BatteryInspectionRequest {
    [Range(1,int.MaxValue)] public int Version { get; set; }
    public DateTimeOffset? ObservedAt { get; set; }
    [Required, RegularExpression("GOOD|LOW|EMPTY|UNKNOWN")] public string BatteryState { get; set; } = "UNKNOWN";
    [Required, RegularExpression("NORMAL|INTERMITTENT|NOT_WORKING|UNKNOWN")] public string OperationState { get; set; } = "UNKNOWN";
    [Range(typeof(decimal),"0","99999.999")] public decimal? VoltageV { get; set; }
    [Range(typeof(decimal),"0","100")] public decimal? BatteryPercent { get; set; }
    [StringLength(80)] public string? MeasurementMethod { get; set; }
    [StringLength(50)] public string? SymptomCode { get; set; }
    [StringLength(2000)] public string? Note { get; set; }
    [Range(1,int.MaxValue)] public int? SessionId { get; set; }
}
public class BatteryServiceRequest {
    [Range(1,int.MaxValue)] public int Version { get; set; }
    public DateTimeOffset? PerformedAt { get; set; }
    [Required, RegularExpression("REPLACE_BATTERY|RECHARGE|CHECK|REPAIR")] public string Action { get; set; } = "";
    [StringLength(80)] public string? BatteryType { get; set; }
    [Range(1,100)] public short? BatteryCount { get; set; }
    public bool? RechargeCompleted { get; set; }
    [Required, RegularExpression("NORMAL|STILL_FAULTY|UNKNOWN")] public string Outcome { get; set; } = "UNKNOWN";
    [Required, RegularExpression("BATTERY|DEVICE|UNKNOWN")] public string ConfirmedCause { get; set; } = "UNKNOWN";
    [StringLength(2000)] public string? Note { get; set; }
}
