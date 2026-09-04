using ClassHub_API.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Globalization;

namespace ClassHub_API.Controllers
{
    [Route("api/admin/history")]
    [ApiController]
    [Authorize(Roles = "ADMIN")]
    public class AdminHistoryController : ControllerBase
    {
        private static readonly string[] ValidStatuses =
        {
            "PENDING",
            "ACTIVE",
            "IN_USE",
            "RETURNING",
            "COMPLETED",
            "CANCELED",
            "EXPIRED",
            "OVERDUE",
            "FAULT"
        };

        private readonly AppDbContext _context;

        public AdminHistoryController(AppDbContext context)
        {
            _context = context;
        }

        [HttpGet]
        public async Task<IActionResult> GetHistory(
            [FromQuery] string? search,
            [FromQuery] string? status,
            [FromQuery] string? fromDate,
            [FromQuery] string? toDate)
        {
            string? normalizedStatus = NormalizeStatus(status);

            if (!string.IsNullOrWhiteSpace(status) && normalizedStatus == "INVALID")
                return BadRequest(new { message = "Trạng thái lọc không hợp lệ." });

            DateTime? startDate = null;
            DateTime? endDate = null;

            if (!string.IsNullOrWhiteSpace(fromDate))
            {
                if (!TryParseDate(fromDate, out DateTime parsedStartDate))
                    return BadRequest(new { message = "Ngày bắt đầu không hợp lệ." });

                startDate = parsedStartDate.Date;
            }

            if (!string.IsNullOrWhiteSpace(toDate))
            {
                if (!TryParseDate(toDate, out DateTime parsedEndDate))
                    return BadRequest(new { message = "Ngày kết thúc không hợp lệ." });

                endDate = parsedEndDate.Date;
            }

            if (startDate.HasValue && endDate.HasValue && startDate > endDate)
                return BadRequest(new { message = "Ngày bắt đầu không được lớn hơn ngày kết thúc." });

            var query = _context.phieu_muon
                .AsNoTracking()
                .Include(p => p.MaPhongNavigation)
                .Include(p => p.MaSvNavigation)
                .Include(p => p.MaSvTraNavigation)
                .Include(p => p.MaSvUyQuyenNavigation)
                .AsQueryable();

            if (normalizedStatus == "IN_USE")
            {
                query = query.Where(p => p.trang_thai == "ACTIVE" || p.trang_thai == "IN_USE");
            }
            else if (!string.IsNullOrWhiteSpace(normalizedStatus))
            {
                query = query.Where(p => p.trang_thai == normalizedStatus);
            }

            if (startDate.HasValue)
            {
                query = query.Where(p => p.ngay_muon >= startDate.Value);
            }

            if (endDate.HasValue)
            {
                DateTime exclusiveEndDate = endDate.Value.AddDays(1);
                query = query.Where(p => p.ngay_muon < exclusiveEndDate);
            }

            if (!string.IsNullOrWhiteSpace(search))
            {
                string keyword = search.Trim();

                if (keyword.Length > 100)
                    return BadRequest(new { message = "Từ khóa tìm kiếm không được vượt quá 100 ký tự." });

                query = query.Where(p =>
                    p.ma_phong.Contains(keyword)
                    || (p.MaPhongNavigation != null && p.MaPhongNavigation.ten_phong.Contains(keyword))
                    || p.ma_sv.Contains(keyword)
                    || (p.MaSvNavigation != null && p.MaSvNavigation.ho_ten.Contains(keyword))
                    || (p.MaSvNavigation != null
                        && p.MaSvNavigation.sdt != null
                        && p.MaSvNavigation.sdt.Contains(keyword))
                    || (p.ma_sv_tra != null && p.ma_sv_tra.Contains(keyword))
                    || (p.MaSvTraNavigation != null && p.MaSvTraNavigation.ho_ten.Contains(keyword))
                    || (p.ma_sv_uy_quyen != null && p.ma_sv_uy_quyen.Contains(keyword))
                    || (p.MaSvUyQuyenNavigation != null
                        && p.MaSvUyQuyenNavigation.ho_ten.Contains(keyword)));
            }

            var borrowingSessions = await query
                .OrderByDescending(p => p.thoi_gian_tao)
                .ToListAsync();

            var result = borrowingSessions.Select(session =>
            {
                var borrower = session.MaSvNavigation;
                var returner = session.MaSvTraNavigation;
                var delegatedUser = session.MaSvUyQuyenNavigation;
                var room = session.MaPhongNavigation;

                return new
                {
                    id = session.id,
                    roomId = session.ma_phong,
                    room = room?.ten_phong ?? session.ma_phong,
                    slot = $"Ca {session.ca_muon}",
                    date = session.ngay_muon.ToString("yyyy-MM-dd"),
                    displayDate = session.ngay_muon.ToString("dd/MM/yyyy"),

                    borrower = borrower?.ho_ten ?? session.ma_sv,
                    borrowerId = session.ma_sv,
                    phoneBorrow = borrower?.sdt ?? "---",
                    timeBorrow = session.thoi_gian_nhan?.ToString("HH:mm") ?? "---",

                    returner = returner?.ho_ten ?? session.ma_sv_tra ?? "---",
                    returnerId = session.ma_sv_tra ?? "---",
                    phoneReturn = returner?.sdt ?? "---",
                    timeReturn = session.thoi_gian_tra?.ToString("HH:mm") ?? "---",

                    status = GetDisplayStatus(session.trang_thai),
                    returnCondition = session.tinh_trang_tra ?? "BINH_THUONG",
                    returnNote = session.ghi_chu_tra ?? string.Empty,
                    cancellationReason = session.ly_do_huy ?? string.Empty,

                    delegateTo = delegatedUser?.ho_ten,
                    delegateId = session.ma_sv_uy_quyen,

                    createdAt = session.thoi_gian_tao.ToString("dd/MM/yyyy HH:mm:ss")
                };
            });

            return Ok(result);
        }

        private static string? NormalizeStatus(string? status)
        {
            if (string.IsNullOrWhiteSpace(status)) return null;

            string normalizedStatus = status.Trim().ToUpperInvariant();

            if (normalizedStatus is "TẤT CẢ" or "TAT CA" or "ALL")
                return null;

            if (normalizedStatus is "ACTIVE" or "IN_USE")
                return "IN_USE";

            return ValidStatuses.Contains(normalizedStatus)
                ? normalizedStatus
                : "INVALID";
        }

        private static string GetDisplayStatus(string status)
        {
            return status switch
            {
                "ACTIVE" => "IN_USE",
                "IN_USE" => "IN_USE",
                "PENDING" => "PENDING",
                "RETURNING" => "RETURNING",
                "COMPLETED" => "COMPLETED",
                "CANCELED" => "CANCELED",
                "EXPIRED" => "EXPIRED",
                "OVERDUE" => "OVERDUE",
                "FAULT" => "FAULT",
                _ => status
            };
        }

        private static bool TryParseDate(string value, out DateTime result)
        {
            string[] acceptedFormats =
            {
                "yyyy-MM-dd",
                "dd-MM-yyyy",
                "dd/MM/yyyy"
            };

            return DateTime.TryParseExact(
                value.Trim(),
                acceptedFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out result);
        }
    }
}