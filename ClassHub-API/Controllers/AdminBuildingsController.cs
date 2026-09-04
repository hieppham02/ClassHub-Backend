using ClassHub_API.Data;
using ClassHub_API.DTOs;
using ClassHub_API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;

namespace ClassHub_API.Controllers
{
    [Route("api/admin")]
    [ApiController]
    [Authorize] // Bắt buộc đăng nhập quyền Admin
    public class AdminBuildingsController : ControllerBase
    {
        private readonly AppDbContext _context;

        public AdminBuildingsController(AppDbContext context)
        {
            _context = context;
        }

        // =========================================================================
        // 1. QUẢN LÝ TÒA NHÀ (BUILDINGS)
        // =========================================================================

        [HttpGet("buildings")]
        public async Task<IActionResult> GetAllBuildings()
        {
            var buildings = await _context.toa_nha
                .Select(b => new
                {
                    id = b.ma_toa_nha,
                    name = b.ten_toa_nha,
                    floors = b.so_tang ?? 5,
                    rooms = _context.phong_hoc.Count(p => p.ma_toa_nha == b.ma_toa_nha),
                    description = b.mo_ta ?? ""
                })
                .ToListAsync();

            return Ok(buildings);
        }

        [HttpPost("buildings")]
        public async Task<IActionResult> CreateBuilding([FromBody] CreateBuildingDTO dto)
        {
            if (string.IsNullOrWhiteSpace(dto.Id) || string.IsNullOrWhiteSpace(dto.Name))
            {
                return BadRequest(new { message = "Vui lòng nhập đầy đủ Mã tòa và Tên tòa nhà!" });
            }

            var trimmedId = dto.Id.Trim().ToUpper();
            if (await _context.toa_nha.AnyAsync(b => b.ma_toa_nha == trimmedId))
            {
                return BadRequest(new { message = "Mã tòa nhà này đã tồn tại!" });
            }

            var building = new ToaNha
            {
                ma_toa_nha = trimmedId,
                ten_toa_nha = dto.Name.Trim(),
                so_tang = dto.Floors > 0 ? dto.Floors : 5,
                mo_ta = dto.Description?.Trim()
            };

            _context.toa_nha.Add(building);
            await _context.SaveChangesAsync();

            return Ok(new { message = $"Đã thêm tòa nhà {building.ten_toa_nha} thành công!" });
        }

        [HttpPut("buildings/{id}")]
        public async Task<IActionResult> UpdateBuilding(string id, [FromBody] UpdateBuildingDTO dto)
        {
            var building = await _context.toa_nha.FindAsync(id);
            if (building == null) return NotFound(new { message = "Không tìm thấy tòa nhà!" });

            building.ten_toa_nha = dto.Name.Trim();
            building.so_tang = dto.Floors;
            building.mo_ta = dto.Description?.Trim();

            await _context.SaveChangesAsync();
            return Ok(new { message = "Cập nhật tòa nhà thành công!" });
        }

        [HttpDelete("buildings/{id}")]
        public async Task<IActionResult> DeleteBuilding(string id)
        {
            var building = await _context.toa_nha.FindAsync(id);
            if (building == null) return NotFound(new { message = "Không tìm thấy tòa nhà!" });

            var hasRooms = await _context.phong_hoc.AnyAsync(p => p.ma_toa_nha == id);
            if (hasRooms)
            {
                return BadRequest(new { message = "Không thể xóa: Tòa nhà này đang có các phòng học trực thuộc!" });
            }

            _context.toa_nha.Remove(building);
            await _context.SaveChangesAsync();

            return Ok(new { message = "Đã xóa tòa nhà thành công!" });
        }

        // =========================================================================
        // 2. QUẢN LÝ PHÒNG HỌC (ROOMS)
        // =========================================================================

        [HttpGet("rooms")]
        public async Task<IActionResult> GetAllRooms()
        {
            var today = DateTime.Today;
            var tomorrow = today.AddDays(1);

            var activeBookings = await _context.phieu_muon
                .Where(p => (p.trang_thai == "ACTIVE" || p.trang_thai == "PENDING" || p.trang_thai == "IN_USE")
                         && p.ngay_muon >= today && p.ngay_muon < tomorrow)
                .Select(p => p.ma_phong)
                .ToListAsync();

            var rooms = await _context.phong_hoc
                .Include(p => p.MaToaNhaNavigation)
                .Select(p => new
                {
                    id = p.ma_phong,
                    name = p.ten_phong,
                    buildingId = p.ma_toa_nha,
                    building = p.MaToaNhaNavigation != null ? p.MaToaNhaNavigation.ten_toa_nha : p.ma_toa_nha,
                    floor = $"Tầng {p.tang}",
                    floorNum = p.tang,
                    capacity = p.suc_chua ?? 70,
                    rawStatus = p.trang_thai,
                    status = p.trang_thai == "BAO_TRI" ? "Bảo trì" :
                             p.trang_thai == "TAM_KHOA" ? "Tạm khóa" :
                             activeBookings.Contains(p.ma_phong) ? "Đang mượn" : "Sẵn sàng"
                })
                .ToListAsync();

            return Ok(rooms);
        }

        [HttpPost("rooms")]
        public async Task<IActionResult> CreateRoom([FromBody] CreateRoomDTO dto)
        {
            if (string.IsNullOrWhiteSpace(dto.Id) || string.IsNullOrWhiteSpace(dto.Name) || string.IsNullOrWhiteSpace(dto.BuildingId))
            {
                return BadRequest(new { message = "Vui lòng nhập đầy đủ Mã phòng, Tên phòng và Tòa nhà!" });
            }

            var trimmedId = dto.Id.Trim().ToUpper();
            if (await _context.phong_hoc.AnyAsync(p => p.ma_phong == trimmedId))
            {
                return BadRequest(new { message = "Mã phòng học này đã tồn tại!" });
            }

            var room = new PhongHoc
            {
                ma_phong = trimmedId,
                ten_phong = dto.Name.Trim(),
                tang = dto.Floor > 0 ? dto.Floor : 1,
                suc_chua = dto.Capacity > 0 ? dto.Capacity : 70,
                ma_toa_nha = dto.BuildingId,
                trang_thai = dto.Status ?? "HOAT_DONG"
            };

            _context.phong_hoc.Add(room);

            // Tự động sinh Tủ đồ IoT thông minh tương ứng với phòng mới tạo (1 phòng = 1 tủ)
            string macHash;
            using (var md5 = MD5.Create())
            {
                var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(trimmedId));
                macHash = Convert.ToHexString(hash);
            }
            string uniqueMac = $"24:6F:28:{macHash.Substring(0, 2)}:{macHash.Substring(2, 2)}:{macHash.Substring(4, 2)}";

            var cabinet = new ThietBiIot
            {
                ma_phong = trimmedId,
                ten_tu = $"Tủ SmartHub {room.ten_phong}",
                mac_address = uniqueMac,
                mqtt_topic = $"classhub/cabinet/{trimmedId}",
                trang_thai_mang = true,
                trang_thai_khoa = "LOCKED",
                firmware_version = "v1.2.0",
                lan_cuoi_online = DateTime.Now
            };

            _context.thiet_bi_iot.Add(cabinet);
            await _context.SaveChangesAsync();

            return Ok(new { message = $"Đã thêm phòng {room.ten_phong} và kích hoạt Tủ thông minh IoT thành công!" });
        }

        [HttpPut("rooms/{id}")]
        public async Task<IActionResult> UpdateRoom(string id, [FromBody] UpdateRoomDTO dto)
        {
            var room = await _context.phong_hoc.FindAsync(id);
            if (room == null) return NotFound(new { message = "Không tìm thấy phòng học!" });

            room.ten_phong = dto.Name.Trim();
            room.tang = dto.Floor;
            room.suc_chua = dto.Capacity;
            room.ma_toa_nha = dto.BuildingId;
            room.trang_thai = dto.Status ?? room.trang_thai;

            await _context.SaveChangesAsync();
            return Ok(new { message = "Cập nhật phòng học thành công!" });
        }

        [HttpDelete("rooms/{id}")]
        public async Task<IActionResult> DeleteRoom(string id)
        {
            var room = await _context.phong_hoc.FindAsync(id);
            if (room == null) return NotFound(new { message = "Không tìm thấy phòng học!" });

            var hasBookings = await _context.phieu_muon.AnyAsync(p => p.ma_phong == id);
            if (hasBookings)
            {
                return BadRequest(new { message = "Không thể xóa: Phòng học này đã có lịch sử mượn phòng!" });
            }

            _context.phong_hoc.Remove(room);
            await _context.SaveChangesAsync();

            return Ok(new { message = "Đã xóa phòng học thành công!" });
        }
    }
}