namespace ClassHub_API.Services
{
    public enum LogAction
    {
        TAO_PHIEU_MUON,
        MO_TU_IOT,
        TRA_PHONG,
        DANG_NHAP,
        KHOA_TAI_KHOAN
    }

    public static class Helper
    {
        public static string GetClientIp(HttpContext context)
        {
            return context.Request.Headers["X-Forwarded-For"].FirstOrDefault()
                   ?? context.Connection.RemoteIpAddress?.ToString()
                   ?? "127.0.0.1";
        }

        public static string GetClientOs(HttpRequest request)
        {
            var userAgent = request.Headers["User-Agent"].ToString();
            if (string.IsNullOrWhiteSpace(userAgent)) return "Unknown";

            if (userAgent.Contains("Windows NT 10.0")) return "Windows 10/11";
            if (userAgent.Contains("Windows NT 6.3")) return "Windows 8.1";
            if (userAgent.Contains("Windows NT 6.2")) return "Windows 8";
            if (userAgent.Contains("Windows NT 6.1")) return "Windows 7";
            if (userAgent.Contains("Mac OS X")) return "macOS";
            if (userAgent.Contains("Android")) return "Android";
            if (userAgent.Contains("iPhone") || userAgent.Contains("iPad")) return "iOS";
            if (userAgent.Contains("Linux")) return "Linux";

            return "Other";
        }
    }
}
