using ClassHub_API.Data;
using ClassHub_API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClassHub_API.Controllers
{
    [Route("api/admin/dashboard")]
    [ApiController]
    [Authorize(Roles = "ADMIN")]
    public class AdminDashboardController : ControllerBase
    {
        private static readonly string[] ActiveStatuses = { "PENDING", "ACTIVE", "IN_USE", "RETURNING" };

        private readonly AppDbContext _context;

        public AdminDashboardController(AppDbContext context)
        {
            _context = context;
        }

        [HttpGet("overview")]
        public async Task<IActionResult> GetOverview([FromQuery] string timeFilter = "Today")
        {
            DateTime now = DateTime.Now;
            DateTime today = DateTime.Today;
            DateTime tomorrow = today.AddDays(1);

            (DateTime startTime, DateTime endTime) = GetTimeRange(timeFilter, now, today, tomorrow);

            int totalBorrows = await _context.phieu_muon
                .AsNoTracking()
                .CountAsync(p => p.thoi_gian_tao >= startTime && p.thoi_gian_tao < endTime);

            int totalCabinets = await _context.thiet_bi_iot
                .AsNoTracking()
                .CountAsync();

            int activeBorrows = await _context.phieu_muon
                .AsNoTracking()
                .CountAsync(p => ActiveStatuses.Contains(p.trang_thai));

            int todayBorrows = await _context.phieu_muon
                .AsNoTracking()
                .CountAsync(p => p.thoi_gian_tao >= today && p.thoi_gian_tao < tomorrow);

            int maintenanceCount = await _context.thiet_bi_iot
                .AsNoTracking()
                .CountAsync(c => c.trang_thai_khoa == "ERROR"
                    || (c.MaPhongNavigation != null
                        && (c.MaPhongNavigation.trang_thai == "BAO_TRI"
                            || c.MaPhongNavigation.trang_thai == "TAM_KHOA")));

            DateTime chartStartDate = today.AddDays(-5);

            var chartBorrowingDates = await _context.phieu_muon
                .AsNoTracking()
                .Where(p => p.thoi_gian_tao >= chartStartDate && p.thoi_gian_tao < tomorrow)
                .Select(p => p.thoi_gian_tao)
                .ToListAsync();

            var chartCounts = chartBorrowingDates
                .GroupBy(date => date.Date)
                .ToDictionary(group => group.Key, group => group.Count());

            var chartSeries = new List<int>();
            var chartCategories = new List<string>();

            for (int dayOffset = 0; dayOffset < 6; dayOffset++)
            {
                DateTime targetDate = chartStartDate.AddDays(dayOffset);

                chartSeries.Add(chartCounts.GetValueOrDefault(targetDate, 0));
                chartCategories.Add(GetVietnameseDayName(targetDate));
            }

            return Ok(new
            {
                stats = new
                {
                    totalBorrows,
                    totalCabinets,
                    activeBorrows,
                    todayBorrows,
                    maintenance = maintenanceCount
                },
                chartSeries,
                chartCategories
            });
        }

        [HttpGet("top-borrowers")]
        public async Task<IActionResult> GetTopBorrowers()
        {
            var borrowingCounts = await _context.phieu_muon
                .AsNoTracking()
                .Where(p => p.trang_thai != "CANCELED")
                .GroupBy(p => p.ma_sv)
                .Select(group => new
                {
                    UserId = group.Key,
                    Total = group.Count()
                })
                .OrderByDescending(item => item.Total)
                .ThenBy(item => item.UserId)
                .Take(5)
                .ToListAsync();

            var userIds = borrowingCounts.Select(item => item.UserId).ToList();

            var users = await _context.tai_khoan
                .AsNoTracking()
                .Where(user => userIds.Contains(user.ma_sv))
                .Select(user => new
                {
                    user.ma_sv,
                    user.ho_ten
                })
                .ToDictionaryAsync(user => user.ma_sv);

            var result = borrowingCounts.Select((item, index) =>
            {
                int rank = index + 1;
                users.TryGetValue(item.UserId, out var user);

                return new
                {
                    rank,
                    name = user?.ho_ten ?? item.UserId,
                    id = item.UserId,
                    total = item.Total,
                    avatarBg = GetRankStyle(rank)
                };
            });

            return Ok(result);
        }

        [HttpGet("recent-activities")]
        public async Task<IActionResult> GetRecentActivities()
        {
            var logs = await _context.nhat_ky_he_thong
                .AsNoTracking()
                .OrderByDescending(log => log.thoi_gian)
                .Take(6)
                .ToListAsync();

            var userIds = logs
                .Where(log => !string.IsNullOrWhiteSpace(log.ma_sv))
                .Select(log => log.ma_sv!)
                .Distinct()
                .ToList();

            var users = await _context.tai_khoan
                .AsNoTracking()
                .Where(user => userIds.Contains(user.ma_sv))
                .Select(user => new
                {
                    user.ma_sv,
                    user.ho_ten
                })
                .ToDictionaryAsync(user => user.ma_sv);

            DateTime now = DateTime.Now;

            var activities = logs.Select(log =>
            {
                users.TryGetValue(log.ma_sv ?? string.Empty, out var user);

                string userName = user != null
                    ? $"{user.ho_ten} ({user.ma_sv})"
                    : log.ma_sv ?? "Hệ thống";

                (string color, string background) = GetActivityStyle(log.hanh_dong);

                return new
                {
                    id = log.id,
                    action = GetActionText(log.hanh_dong),
                    user = userName,
                    details = log.chi_tiet ?? "Hoạt động được ghi nhận trên hệ thống.",
                    time = GetTimeAgo(log.thoi_gian, now),
                    color,
                    bg = background,
                    ip_address = log.ip_address,
                    user_agent = log.user_agent
                };
            });

            return Ok(activities);
        }

        private static (DateTime StartTime, DateTime EndTime) GetTimeRange(
            string? timeFilter,
            DateTime now,
            DateTime today,
            DateTime tomorrow)
        {
            return timeFilter?.Trim().ToUpperInvariant() switch
            {
                "24H" => (now.AddHours(-24), now),
                "7D" => (today.AddDays(-6), tomorrow),
                "30D" => (today.AddDays(-29), tomorrow),
                "60D" => (today.AddDays(-59), tomorrow),
                _ => (today, tomorrow)
            };
        }

        private static string GetVietnameseDayName(DateTime date)
        {
            return date.DayOfWeek switch
            {
                DayOfWeek.Monday => "Thứ 2",
                DayOfWeek.Tuesday => "Thứ 3",
                DayOfWeek.Wednesday => "Thứ 4",
                DayOfWeek.Thursday => "Thứ 5",
                DayOfWeek.Friday => "Thứ 6",
                DayOfWeek.Saturday => "Thứ 7",
                DayOfWeek.Sunday => "CN",
                _ => date.ToString("dd/MM")
            };
        }

        private static string GetRankStyle(int rank)
        {
            return rank switch
            {
                1 => "bg-amber-500 text-white",
                2 => "bg-slate-300 text-slate-700",
                3 => "bg-amber-700/20 text-amber-800",
                _ => "bg-slate-100 text-slate-600"
            };
        }

        private static string GetActionText(string action)
        {
            return action switch
            {
                nameof(LogAction.TAO_PHIEU_MUON) => "Đăng ký mượn thiết bị",
                nameof(LogAction.MO_TU_IOT) => "Mở tủ nhận thiết bị",
                nameof(LogAction.TRA_PHONG) => "Hoàn tất trả thiết bị",
                nameof(LogAction.DANG_NHAP) => "Đăng nhập hệ thống",
                nameof(LogAction.KHOA_TAI_KHOAN) => "Khóa tài khoản",
                "YEU_CAU_TRA_THIET_BI" => "Yêu cầu trả thiết bị",
                "ADMIN_YEU_CAU_MO_TU" => "Quản trị viên yêu cầu mở tủ",
                "ADMIN_YEU_CAU_KHOA_TU" => "Quản trị viên yêu cầu khóa tủ",
                "TAO_TAI_KHOAN" => "Tạo tài khoản",
                "CAP_NHAT_TAI_KHOAN" => "Cập nhật tài khoản",
                "MO_KHOA_TAI_KHOAN" => "Mở khóa tài khoản",
                "VO_HIEU_HOA_TAI_KHOAN" => "Vô hiệu hóa tài khoản",
                "BAT_BAO_TRI_TU" => "Bật chế độ bảo trì",
                "TAT_BAO_TRI_TU" => "Tắt chế độ bảo trì",
                _ => action
            };
        }

        private static (string Color, string Background) GetActivityStyle(string action)
        {
            if (action.Contains("KHOA") || action.Contains("VO_HIEU_HOA"))
                return ("text-red-600", "bg-red-100");

            if (action.Contains("TRA"))
                return ("text-emerald-600", "bg-emerald-100");

            if (action.Contains("MO_TU") || action.Contains("TAO"))
                return ("text-amber-600", "bg-amber-100");

            if (action.Contains("BAO_TRI"))
                return ("text-orange-600", "bg-orange-100");

            return ("text-blue-600", "bg-blue-100");
        }

        private static string GetTimeAgo(DateTime actionTime, DateTime now)
        {
            TimeSpan elapsed = now - actionTime;

            if (elapsed.TotalSeconds < 0) return "Vừa xong";
            if (elapsed.TotalMinutes < 1) return "Vừa xong";
            if (elapsed.TotalMinutes < 60) return $"{(int)elapsed.TotalMinutes} phút trước";
            if (elapsed.TotalHours < 24) return $"{(int)elapsed.TotalHours} giờ trước";

            return $"{(int)elapsed.TotalDays} ngày trước";
        }
    }
}