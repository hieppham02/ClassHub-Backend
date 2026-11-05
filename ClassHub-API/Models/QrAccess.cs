using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ClassHub_API.Models;

[Table("cabinet_qr_access")]
public class QrAccess
{
    [Key, Column("id")] public int Id { get; set; }
    [Column("session_id")] public int SessionId { get; set; }
    [Column("token_hash"), MaxLength(64)] public string TokenHash { get; set; } = "";
    [Column("purpose"), MaxLength(10)] public string Purpose { get; set; } = "";
    [Column("state"), MaxLength(20)] public string State { get; set; } = "ISSUED";
    [Column("expires_at")] public DateTime ExpiresAt { get; set; }
    [Column("created_at")] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    [Column("used_at")] public DateTime? UsedAt { get; set; }
    [Column("command_id"), MaxLength(32)] public string? CommandId { get; set; }
    [Column("rfid_passed")] public bool? RfidPassed { get; set; }
    [Column("camera_passed")] public bool? CameraPassed { get; set; }
    [Column("verification_sequence")] public long VerificationSequence { get; set; }
    [Column("rfid_expected_json", TypeName = "longtext")] public string? RfidExpectedJson { get; set; }
    [Column("rfid_observed_json", TypeName = "longtext")] public string? RfidObservedJson { get; set; }
    [Column("rfid_sequence")] public int RfidSequence { get; set; }
    [Column("device_revision")] public long DeviceRevision { get; set; }
    [Column("device_phase"), MaxLength(16)] public string? DevicePhase { get; set; }
}
