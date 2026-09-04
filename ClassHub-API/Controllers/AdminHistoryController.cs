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
            var query = _context.phieu_muon
                .Include(p => p.MaPhongNavigation)
                .Include(p => p.MaSvNavigation)
                .Include(p => p.MaSvTraNavigation)
                .Include(p => p.MaSvUyQuyenNavigation)
                .AsQueryable();

            // 1. Lọc theo trạng thái
            if (!string.IsNullOrWhiteSpace(status) && status != "Tất cả")
            {
                if (status == "IN_USE" || status == "ACTIVE")
                {
                    query = query.Where(p => p.trang_thai == "ACTIVE" || p.trang_thai == "IN_USE");
                }
                else
                {
                    query = query.Where(p => p.trang_thai == status);
                }
            }

            // 2. Lọc Từ ngày (fromDate)
            if (!string.IsNullOrWhiteSpace(fromDate) && DateTime.TryParse(fromDate, out DateTime startD))
            {
                query = query.Where(p => p.ngay_muon >= startD.Date);
            }

            // 3. Lọc Đến ngày (toDate)
            if (!string.IsNullOrWhiteSpace(toDate) && DateTime.TryParse(toDate, out DateTime endD))
            {
                query = query.Where(p => p.ngay_muon <= endD.Date);
            }

            // 4. Lọc theo từ khóa tìm kiếm (Tên phòng, Mã phòng, Người mượn, Người trả, SĐT)
            if (!string.IsNullOrWhiteSpace(search))
            {
                var kw = search.Trim().ToLower();
                query = query.Where(p =>
                    p.ma_phong.ToLower().Contains(kw) ||
                    (p.MaPhongNavigation != null && p.MaPhongNavigation.ten_phong.ToLower().Contains(kw)) ||
                    (p.MaSvNavigation != null && p.MaSvNavigation.ho_ten.ToLower().Contains(kw)) ||
                    p.ma_sv.ToLower().Contains(kw) ||
                    (p.MaSvNavigation != null && p.MaSvNavigation.sdt != null && p.MaSvNavigation.sdt.Contains(kw)) ||
                    (p.MaSvTraNavigation != null && p.MaSvTraNavigation.ho_ten.ToLower().Contains(kw)) ||
                    (p.ma_sv_tra != null && p.ma_sv_tra.ToLower().Contains(kw))
                );
            }

            // Sắp xếp đơn mới nhất lên đầu
            var rawList = await query
                .OrderByDescending(p => p.thoi_gian_tao)
                .ToListAsync();

            var result = rawList.Select(p =>
            {
                var borrower = p.MaSvNavigation;
                var returner = p.MaSvTraNavigation;
                var delegateUser = p.MaSvUyQuyenNavigation;
                var room = p.MaPhongNavigation;

                // Chuẩn hóa trạng thái hiển thị
                string displayStatus = p.trang_thai switch
                {
                    "ACTIVE" => "IN_USE",
                    "IN_USE" => "IN_USE",
                    "PENDING" => "PENDING",
                    "COMPLETED" => "COMPLETED",
                    "CANCELED" => "CANCELED",
                    "OVERDUE" => "OVERDUE",
                    _ => p.trang_thai
                };

                return new
                {
                    id = p.id,
                    roomId = p.ma_phong,
                    room = room?.ten_phong ?? p.ma_phong,
                    slot = $"Ca {p.ca_muon}",
                    date = p.ngay_muon.ToString("yyyy-MM-dd"),
                    displayDate = p.ngay_muon.ToString("dd/MM/yyyy"),

                    // Thông tin người mượn
                    borrower = borrower?.ho_ten ?? p.ma_sv,
                    borrowerId = p.ma_sv,
                    phoneBorrow = borrower?.sdt ?? "---",
                    timeBorrow = p.thoi_gian_nhan.HasValue
                        ? p.thoi_gian_nhan.Value.ToString("HH:mm")
                        : p.thoi_gian_tao.ToString("HH:mm"),

                    // Thông tin người trả
                    returner = returner?.ho_ten ?? (p.ma_sv_tra != null ? p.ma_sv_tra : "---"),
                    returnerId = p.ma_sv_tra ?? "---",
                    phoneReturn = returner?.sdt ?? "---",
                    timeReturn = p.thoi_gian_tra.HasValue ? p.thoi_gian_tra.Value.ToString("HH:mm") : "---",

                    // Chi tiết hoàn trả
                    status = displayStatus,
                    returnCondition = p.tinh_trang_tra ?? "BINH_THUONG",
                    returnNote = p.ghi_chu_tra ?? "",
                    delegateTo = delegateUser?.ho_ten
                };
            }).ToList();

            return Ok(result);
        }
    }
}