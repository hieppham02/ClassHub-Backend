using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ClassHub_API.Models;

[Table("return_inspection")]
public class ReturnInspection
{
    [Key, Column("id")] public int Id { get; set; }
    [Column("session_id")] public int SessionId { get; set; }
    [Column("command_id"), MaxLength(32)] public string CommandId { get; set; } = "";
    [Column("status"), MaxLength(24)] public string Status { get; set; } = "REVIEW";
    [Column("reason"), MaxLength(500)] public string Reason { get; set; } = "";
    [Column("captured_at_utc")] public DateTime CapturedAtUtc { get; set; } = DateTime.UtcNow;
    [Column("image_jpeg", TypeName = "mediumblob")] public byte[]? ImageJpeg { get; set; }
    [Column("image_sha256"), MaxLength(64)] public string? ImageSha256 { get; set; }
    [Column("camera_sequence")] public long? CameraSequence { get; set; }
    [Column("expected_json", TypeName = "longtext")] public string ExpectedJson { get; set; } = "[]";
    [Column("analysis_json", TypeName = "longtext")] public string? AnalysisJson { get; set; }
    [Column("reviewed_by"), MaxLength(100)] public string? ReviewedBy { get; set; }
    [Column("reviewed_at_utc")] public DateTime? ReviewedAtUtc { get; set; }
    [Column("review_json", TypeName = "longtext")] public string? ReviewJson { get; set; }
}
