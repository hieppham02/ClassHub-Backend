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
            var danhSach = await (from p in _context.phong_hoc
                                  join iot in _context.thiet_bi_iot on p.ma_phong equals iot.ma_phong into iotGroup
                                  from iot in iotGroup.DefaultIfEmpty()
                                  select new
                                  {
                                      id = p.ma_phong,
                                      building = _context.toa_nha
                                          .Where(t => t.ma_toa_nha == p.ma_toa_nha)
                                          .Select(t => t.ten_toa_nha)
                                          .FirstOrDefault() ?? p.ma_toa_nha,
                                      floor = p.ten_phong.Contains("-")
                                          ? "Tầng " + p.ten_phong.Substring(p.ten_phong.IndexOf("-") + 1, 1)
                                          : "Tầng 1",
                                      name = p.ten_phong,
                                      capacity = p.suc_chua ?? 0,
                                      // Nếu phòng bị khóa/bảo trì HOẶC thiết bị IoT đang Offline -> Trạng thái MAINTENANCE (Bảo trì)
                                      status = (p.trang_thai == "BAO_TRI" || (iot != null && !iot.trang_thai_mang)) ? "MAINTENANCE" : "AVAILABLE",
                                      isOnline = iot != null && iot.trang_thai_mang,
                                      equipment = _context.thiet_bi
                                          .Where(tb => tb.ma_phong == p.ma_phong)
                                          .Select(tb => tb.ten_thiet_bi)
                                          .ToList(),
                                      tone = "from-blue-600 to-cyan-500"
                                  }).ToListAsync();

            return Ok(danhSach);
        }
    }
}