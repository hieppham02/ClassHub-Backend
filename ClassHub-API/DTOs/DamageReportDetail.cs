namespace ClassHub_API.DTOs;
public record IncidentHistory(string Action, string? ActorId, DateTime AtUtc, string Note);
public class DamageReportDetail
{
    public string RoomId { get; set; } = "";
    public string EquipmentName { get; set; } = "";
    public string Description { get; set; } = "";
    public string Status { get; set; } = "PENDING";
    public string? ResolutionNote { get; set; }
    public string? ResolvedBy { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public string? AssigneeId { get; set; }
    public int? EquipmentId { get; set; }
    public int? SessionId { get; set; }
    public string IncidentType { get; set; } = "GENERAL";
    public bool AllocationBlocked { get; set; }
    public List<IncidentHistory> History { get; set; } = [];
}
