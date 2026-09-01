using ClassHub_API.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClassHub_API.Controllers
{
    [Route("api/admin/statistics")]
    [ApiController]
    [Authorize]
    public class AdminStatisticsController : ControllerBase
    {
        private readonly AppDbContext _context;

        public AdminStatisticsController(AppDbContext context)
        {
            _context = context;
        }

        [HttpGet("overview")]
        public async Task<IActionResult> GetOverview([FromQuery] string timeFilter = "Today")
        {
            var now = DateTime.Now;
            var today = DateTime.Today;
            var tomorrow = today.AddDays(1);

            DateTime startTime = timeFilter switch
            {
                "Today" => today,
                "24h" => now.AddHours(-24),
                "7D" => today.AddDays(-7),
                "30D" => today.AddDays(-30),
                "60D" => today.AddDays(-60),
                _ => today
            };

            // 1. Tổng số lượt mượn trong khoảng thời gian lọc (hoặc toàn bộ nếu filter rộng)
            var totalBorrows = await _context.phieu_muon
                .CountAsync(p => p.thoi_gian_tao >= startTime || p.ngay_muon >= startTime);

            // 2. Tổng số tủ / phòng
            var totalCabinets = await _context.thiet_bi_iot.CountAsync();
            if (totalCabinets == 0)
            {
                totalCabinets = await _context.phong_hoc.CountAsync();
            }

            // 3. Số lượt ĐANG MƯỢN hoặc ĐANG CHỜ LẤY ĐỒ (Active / In-use / Pending)
            var activeBorrows = await _context.phieu_muon
                .CountAsync(p => p.trang_thai == "ACTIVE" || p.trang_thai == "IN_USE" || p.trang_thai == "PENDING");

            // 4. Số lượt mượn HÔM NAY (theo ngày mượn hoặc ngày tạo phiếu)
            var todayBorrows = await _context.phieu_muon
                .CountAsync(p => (p.ngay_muon >= today && p.ngay_muon < tomorrow)
                              || (p.thoi_gian_tao >= today && p.thoi_gian_tao < tomorrow));

            // 5. Phòng hoặc Tủ đang bảo trì / lỗi
            var maintenanceCount = await _context.phong_hoc
                .CountAsync(p => p.trang_thai == "BAO_TRI" || p.trang_thai == "TAM_KHOA");

            // 6. Biểu đồ lượt mượn 6 ngày gần nhất
            var weeklyChartData = new List<int>();
            var dayNames = new List<string>();

            // Lấy 6 ngày gần nhất lùi dần về hôm nay
            for (int i = 5; i >= 0; i--)
            {
                var targetDate = today.AddDays(-i);
                var nextDate = targetDate.AddDays(1);

                var count = await _context.phieu_muon
                    .CountAsync(p => (p.ngay_muon >= targetDate && p.ngay_muon < nextDate)
                                  || (p.thoi_gian_tao >= targetDate && p.thoi_gian_tao < nextDate));

                weeklyChartData.Add(count);

                string dayLabel = targetDate.DayOfWeek switch
                {
                    DayOfWeek.Monday => "Thứ 2",
                    DayOfWeek.Tuesday => "Thứ 3",
                    DayOfWeek.Wednesday => "Thứ 4",
                    DayOfWeek.Thursday => "Thứ 5",
                    DayOfWeek.Friday => "Thứ 6",
                    DayOfWeek.Saturday => "Thứ 7",
                    DayOfWeek.Sunday => "CN",
                    _ => targetDate.ToString("dd/MM")
                };
                dayNames.Add(dayLabel);
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
                chartSeries = weeklyChartData,
                chartCategories = dayNames
            });
        }

        [HttpGet("top-borrowers")]
        public async Task<IActionResult> GetTopBorrowers()
        {
            var topList = await _context.phieu_muon
                .GroupBy(p => p.ma_sv)
                .Select(g => new
                {
                    MaSv = g.Key,
                    Total = g.Count()
                })
                .OrderByDescending(x => x.Total)
                .Take(5)
                .ToListAsync();

            var result = new List<object>();
            int rank = 1;

            foreach (var item in topList)
            {
                var user = await _context.tai_khoan.FirstOrDefaultAsync(u => u.ma_sv == item.MaSv);
                string avatarBg = rank switch
                {
                    1 => "bg-amber-500 text-white",
                    2 => "bg-slate-300 text-slate-700",
                    3 => "bg-amber-700/20 text-amber-800",
                    _ => "bg-slate-100 text-slate-600"
                };

                result.Add(new
                {
                    rank = rank++,
                    name = user?.ho_ten ?? item.MaSv,
                    id = item.MaSv,
                    total = item.Total,
                    avatarBg
                });
            }

            return Ok(result);
        }

        [HttpGet("recent-activities")]
        public async Task<IActionResult> GetRecentActivities()
        {
            var logs = await _context.nhat_ky_he_thong
                .OrderByDescending(l => l.thoi_gian)
                .Take(6)
                .ToListAsync();

            var activities = new List<object>();

            foreach (var log in logs)
            {
                var user = await _context.tai_khoan.FirstOrDefaultAsync(u => u.ma_sv == log.ma_sv);
                string userName = user != null ? $"{user.ho_ten} ({user.ma_sv})" : (log.ma_sv ?? "Hệ thống");

                string actionText = log.hanh_dong switch
                {
                    "TAO_PHIEU_MUON" => "Đăng ký mượn phòng",
                    "MO_TU_IOT" => "Mở tủ nhận thiết bị",
                    "TRA_PHONG" => "Trả phòng & thiết bị",
                    "DANG_NHAP" => "Đăng nhập hệ thống",
                    "KHOA_TAI_KHOAN" => "Khóa tài khoản vi phạm",
                    _ => log.hanh_dong
                };

                string color = "text-blue-600";
                string bg = "bg-blue-100";

                if (log.hanh_dong.Contains("TAO") || log.hanh_dong.Contains("MO_TU"))
                {
                    color = "text-amber-600";
                    bg = "bg-amber-100";
                }
                else if (log.hanh_dong.Contains("TRA"))
                {
                    color = "text-emerald-600";
                    bg = "bg-emerald-100";
                }
                else if (log.hanh_dong.Contains("KHOA"))
                {
                    color = "text-red-600";
                    bg = "bg-red-100";
                }

                var timeSpan = DateTime.Now - log.thoi_gian;
                string timeAgo = timeSpan.TotalMinutes < 60
                    ? $"{(int)Math.Max(1, timeSpan.TotalMinutes)} phút trước"
                    : timeSpan.TotalHours < 24
                        ? $"{(int)timeSpan.TotalHours} giờ trước"
                        : $"{(int)timeSpan.TotalDays} ngày trước";

                activities.Add(new
                {
                    id = log.id,
                    action = actionText,
                    user = userName,
                    details = log.chi_tiet ?? "Hoạt động ghi nhận trên hệ thống",
                    time = timeAgo,
                    color,
                    bg,
                    ip_address = log.ip_address,
                    user_agent = log.user_agent
                });
            }

            return Ok(activities);
        }
    }
}
