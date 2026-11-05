using ClassHub_API.Models;
namespace ClassHub_API.Services;

public record BatteryDecision(string Priority, string Reason, string RecommendedAction, DateTime? EvidenceAtUtc, DateTime? ValidUntilUtc);
public static class BatteryRules {
    public const string Version = "battery-rules-v1";
    // A NORMAL service wins a same-timestamp inspection. UNKNOWN never clears a known fault.
    public static BatteryDecision Evaluate(EquipmentBatteryProfile profile, EquipmentBatteryInspection? battery,
        EquipmentBatteryInspection? operation, EquipmentBatteryService? service, EquipmentBatteryService? cycle,
        DateTime now, double freshHours, double? expectedCycleHours) {
        if (!profile.Enabled) return new("UNKNOWN","Hồ sơ đã ngừng theo dõi.","Không đưa vào lịch kiểm tra.",null,null);
        if (battery?.DataSource != "REAL") battery = null;
        if (operation?.DataSource != "REAL") operation = null;
        if (service?.DataSource != "REAL") service = null;
        if (cycle?.DataSource != "REAL") cycle = null;
        if (battery?.ObservedAtUtc > now) battery = null;
        if (operation?.ObservedAtUtc > now) operation = null;
        if (service?.PerformedAtUtc > now) service = null;
        if (cycle?.PerformedAtUtc > now) cycle = null;
        if (cycle != null && !(cycle.Outcome == "NORMAL" &&
            (cycle.Action == "REPLACE_BATTERY" || cycle.Action == "RECHARGE" && cycle.RechargeCompleted == true))) cycle = null;
        freshHours = double.IsFinite(freshHours) && freshHours is >= 1 and <= 8760 ? freshHours : 168;
        string batteryState = "UNKNOWN", operationState = "UNKNOWN";
        DateTime? batteryAt = null, operationAt = null;
        if (battery != null && battery.BatteryState != "UNKNOWN") {
            batteryState = battery.BatteryState; batteryAt = battery.ObservedAtUtc;
        }
        if (cycle != null && (batteryAt == null || cycle.PerformedAtUtc >= batteryAt)) {
            batteryState = "GOOD"; batteryAt = cycle.PerformedAtUtc;
        }
        if (operation != null && operation.OperationState != "UNKNOWN") {
            operationState = operation.OperationState; operationAt = operation.ObservedAtUtc;
        }
        if (service != null && service.Outcome != "UNKNOWN" && (operationAt == null || service.PerformedAtUtc >= operationAt)) {
            operationState = service.Outcome == "NORMAL" ? "NORMAL" : "NOT_WORKING";
            operationAt = service.PerformedAtUtc;
        }
        var evidenceAt = new[] { batteryAt, operationAt }.Where(x => x.HasValue).Max();
        if (batteryState is "LOW" or "EMPTY")
            return new("HIGH",batteryState == "EMPTY" ? "Lần kiểm tra ghi nhận hết pin; chưa có xác nhận khắc phục." : "Lần kiểm tra ghi nhận pin yếu; chưa có xác nhận khắc phục.",
                "Kiểm tra và thay hoặc sạc pin trước lượt sử dụng tiếp theo.",batteryAt,null);
        if (operationState is "INTERMITTENT" or "NOT_WORKING")
            return new("HIGH","Micro hoạt động chập chờn hoặc có lỗi chưa được xác nhận khắc phục.",
                "Kiểm tra pin và micro; xác nhận nguyên nhân trước khi sửa hoặc thay.",operationAt,null);
        if (cycle != null && expectedCycleHours is > 0 && double.IsFinite(expectedCycleHours.Value)
            && (now-cycle.PerformedAtUtc).TotalHours >= expectedCycleHours) {
            return new("MEDIUM","Đã đến tuổi chu kỳ pin cấu hình, tính từ lần thay hoặc sạc đầy được xác nhận; không phải giờ bật thực tế.",
                "Kiểm tra mức pin trước lượt sử dụng tiếp theo.",cycle.PerformedAtUtc,null);
        }
        if (batteryAt == null || operationAt == null)
            return new("UNKNOWN","Chưa đủ kết quả kiểm tra pin và hoạt động của micro.","Kiểm tra pin và thử micro, sau đó ghi kết quả.",evidenceAt,null);
        var expires = (batteryAt < operationAt ? batteryAt.Value : operationAt.Value).AddHours(freshHours);
        if (expires <= now)
            return new("UNKNOWN","Kết quả kiểm tra đã quá hạn; chưa thể kết luận tình trạng hiện tại.","Kiểm tra lại pin và thử micro.",evidenceAt,null);
        return new("LOW","Pin và hoạt động được xác nhận bình thường trong thời hạn kiểm tra.","Tiếp tục sử dụng; kiểm tra lại theo lịch.",evidenceAt,expires);
    }
}
