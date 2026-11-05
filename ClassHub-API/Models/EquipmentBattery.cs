using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
namespace ClassHub_API.Models;

[Table("equipment_battery_profile")]
public class EquipmentBatteryProfile {
    [Key, Column("id")] public long Id { get; set; }
    [Column("equipment_id")] public int EquipmentId { get; set; }
    [Column("asset_code"), MaxLength(80)] public string AssetCode { get; set; } = "";
    [Column("current_rfid_tag_id")] public int? CurrentRfidTagId { get; set; }
    [Column("battery_type"), MaxLength(80)] public string? BatteryType { get; set; }
    [Column("battery_count")] public short? BatteryCount { get; set; }
    [Column("rechargeable")] public bool? Rechargeable { get; set; }
    [Column("manufacturer"), MaxLength(100)] public string? Manufacturer { get; set; }
    [Column("model"), MaxLength(100)] public string? Model { get; set; }
    [Column("enabled")] public bool Enabled { get; set; } = true;
    [Column("created_at_utc")] public DateTime CreatedAtUtc { get; set; }
    [Column("updated_at_utc")] public DateTime UpdatedAtUtc { get; set; }
    [Column("version")] public int Version { get; set; } = 1;
}
[Table("equipment_battery_inspection")]
public class EquipmentBatteryInspection {
    [Key, Column("id")] public long Id { get; set; }
    [Column("profile_id")] public long ProfileId { get; set; }
    [Column("observed_at_utc")] public DateTime ObservedAtUtc { get; set; }
    [Column("recorded_at_utc")] public DateTime RecordedAtUtc { get; set; }
    [Column("battery_state"), MaxLength(12)] public string BatteryState { get; set; } = "UNKNOWN";
    [Column("operation_state"), MaxLength(16)] public string OperationState { get; set; } = "UNKNOWN";
    [Column("voltage_v"), Microsoft.EntityFrameworkCore.Precision(8,3)] public decimal? VoltageV { get; set; }
    [Column("battery_percent"), Microsoft.EntityFrameworkCore.Precision(5,2)] public decimal? BatteryPercent { get; set; }
    [Column("measurement_method"), MaxLength(80)] public string? MeasurementMethod { get; set; }
    [Column("symptom_code"), MaxLength(50)] public string? SymptomCode { get; set; }
    [Column("note")] public string? Note { get; set; }
    [Column("session_id")] public int? SessionId { get; set; }
    [Column("recorded_by"), MaxLength(50)] public string? RecordedBy { get; set; }
    [Column("data_source"), MaxLength(4)] public string DataSource { get; set; } = "REAL";
    [Column("batch_id"), MaxLength(36)] public string? BatchId { get; set; }
}
[Table("equipment_battery_service")]
public class EquipmentBatteryService {
    [Key, Column("id")] public long Id { get; set; }
    [Column("profile_id")] public long ProfileId { get; set; }
    [Column("performed_at_utc")] public DateTime PerformedAtUtc { get; set; }
    [Column("recorded_at_utc")] public DateTime RecordedAtUtc { get; set; }
    [Column("action"), MaxLength(20)] public string Action { get; set; } = "";
    [Column("battery_type"), MaxLength(80)] public string? BatteryType { get; set; }
    [Column("battery_count")] public short? BatteryCount { get; set; }
    [Column("recharge_completed")] public bool? RechargeCompleted { get; set; }
    [Column("outcome"), MaxLength(12)] public string Outcome { get; set; } = "UNKNOWN";
    [Column("confirmed_cause"), MaxLength(10)] public string ConfirmedCause { get; set; } = "UNKNOWN";
    [Column("note")] public string? Note { get; set; }
    [Column("performed_by"), MaxLength(50)] public string? PerformedBy { get; set; }
    [Column("data_source"), MaxLength(4)] public string DataSource { get; set; } = "REAL";
    [Column("batch_id"), MaxLength(36)] public string? BatchId { get; set; }
}
[Table("equipment_battery_assessment")]
public class EquipmentBatteryAssessment {
    [Key, Column("id")] public long Id { get; set; }
    [Column("profile_id")] public long ProfileId { get; set; }
    [Column("assessed_at_utc")] public DateTime AssessedAtUtc { get; set; }
    [Column("evidence_cutoff_utc")] public DateTime EvidenceCutoffUtc { get; set; }
    [Column("priority"), MaxLength(8)] public string Priority { get; set; } = "UNKNOWN";
    [Column("recommended_action"), MaxLength(255)] public string RecommendedAction { get; set; } = "";
    [Column("method"), MaxLength(5)] public string Method { get; set; } = "RULE";
    [Column("method_version"), MaxLength(80)] public string MethodVersion { get; set; } = "";
    [Column("model_version"), MaxLength(80)] public string? ModelVersion { get; set; }
    [Column("evidence_json")] public string EvidenceJson { get; set; } = "{}";
    [Column("valid_until_utc")] public DateTime? ValidUntilUtc { get; set; }
    [Column("data_source"), MaxLength(4)] public string DataSource { get; set; } = "REAL";
    [Column("batch_id"), MaxLength(36)] public string? BatchId { get; set; }
}
