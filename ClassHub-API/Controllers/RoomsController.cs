using ClassHub_API.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClassHub_API.Controllers
{
    [Route("api/rooms")]
    [ApiController]
    [Authorize]
    public class RoomsController : ControllerBase
    {
        private readonly AppDbContext _context;

        public RoomsController(AppDbContext context)
        {
            _context = context;
        }

        [HttpGet("get-rooms")]
        public async Task<IActionResult> GetDanhSachPhong()
        {
            var rooms = await _context.phong_hoc
                .AsNoTracking()
                .AsSplitQuery()
                .Include(room => room.MaToaNhaNavigation)
                .Include(room => room.ThietBis)
                .Include(room => room.ThietBiIots)
                .OrderBy(room => room.ma_toa_nha)
                .ThenBy(room => room.tang)
                .ThenBy(room => room.ma_phong)
                .ToListAsync();

            var result = rooms.Select(room =>
            {
                var cabinet = room.ThietBiIots
                    .OrderByDescending(item => item.trang_thai_mang)
                    .ThenBy(item => item.id)
                    .FirstOrDefault();

                bool hasCabinet = cabinet != null;
                bool isOnline = cabinet?.trang_thai_mang ?? false;
                bool hasCabinetError = cabinet?.trang_thai_khoa == "ERROR";

                bool isAvailable = room.trang_thai == "HOAT_DONG"
                    && hasCabinet
                    && isOnline
                    && !hasCabinetError;

                return new
                {
                    id = room.ma_phong,
                    building = room.MaToaNhaNavigation?.ten_toa_nha ?? room.ma_toa_nha ?? "---",
                    buildingId = room.ma_toa_nha,
                    floor = $"Tầng {room.tang}",
                    floorNum = room.tang,
                    name = room.ten_phong,
                    locationType = room.LocationType,
                    capacity = room.suc_chua ?? 0,
                    status = isAvailable ? "AVAILABLE" : "MAINTENANCE",
                    roomStatus = room.trang_thai,
                    hasCabinet,
                    isOnline,
                    lockStatus = cabinet?.trang_thai_khoa,
                    equipment = room.ThietBis.Where(e => e.UsageScope == "CABINET" && e.so_luong > 0)
                        .Select(equipment => equipment.ten_thiet_bi)
                        .Distinct()
                        .OrderBy(name => name)
                        .ToList(),
                    equipmentDetails = room.ThietBis.Where(e => e.UsageScope == "CABINET" && e.so_luong > 0).OrderBy(e => e.id)
                        .Select(e => new { e.id, name = e.ten_thiet_bi, quantity = e.so_luong,
                            condition = e.tinh_trang, note = e.ghi_chu }).ToList(),
                    tone = "from-blue-600 to-cyan-500"
                };
            });

            return Ok(result);
        }
    }
}