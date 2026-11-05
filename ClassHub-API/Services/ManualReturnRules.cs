namespace ClassHub_API.Services;

public static class ManualReturnRules {
    public static string? Validate(string status, string? lockState, bool? sessionOpen, bool? doorOpen, string? purpose, string? qrState) {
        if(status!="RETURNING")return "Chỉ xác nhận thủ công phiếu đang chờ trả.";
        if(lockState!="LOCKED" || sessionOpen==true)return "Đóng và khóa tủ trước khi xác nhận trả.";
        if(doorOpen==true)return "Cảm biến vẫn ghi nhận cửa mở. Đóng cửa trước khi xác nhận trả.";
        if(purpose=="RETURN" && qrState is "USED" or "OPENED")
            return "Tủ đang thực hiện lượt mở/trả. Chờ tủ báo đóng cửa rồi xác nhận.";
        return null;
    }
}
