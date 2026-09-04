using ClassHub_API.Data;
using ClassHub_API.DTOs;
using ClassHub_API.Models;
using ClassHub_API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace ClassHub_API.Controllers
{
    [Route("api/admin")]
    [ApiController]
    [Authorize(Roles = "ADMIN")]
    public class AdminBuildingsController : ControllerBase
    {
        private static readonly string[] ActiveStatuses = { "PENDING", "ACTIVE", "IN_USE", "RETURNING" };
        private static readonly string[] ValidRoomStatuses = { "HOAT_DONG", "BAO_TRI", "TAM_KHOA" };

        private readonly AppDbContext _context;
        private readonly ILogger<AdminBuildingsController> _logger;

        public AdminBuildingsController(AppDbContext context, ILogger<AdminBuildingsController> logger)
        {
            _context = context;
            _logger = logger;
        }

        [HttpGet("buildings")]
        public async Task<IActionResult> GetAllBuildings()
        {
            var buildings = await _context.toa_nha
                .AsNoTracking()
                .OrderBy(building => building.ma_toa_nha)
                .Select(building => new
                {
                    id = building.ma_toa_nha,
                    name = building.ten_toa_nha,
                    floors = building.so_tang ?? 1,
                    rooms = building.PhongHocs.Count,
                    description = building.mo_ta ?? string.Empty
                })
                .ToListAsync();

            return Ok(buildings);
        }

        [HttpPost("buildings")]
        public async Task<IActionResult> CreateBuilding([FromBody] CreateBuildingDTO dto)
        {
            if (dto == null || string.IsNullOrWhiteSpace(dto.Id) || string.IsNullOrWhiteSpace(dto.Name))
                return BadRequest(new { message = "Vui lòng nhập mã và tên tòa nhà." });

            string buildingId = dto.Id.Trim().ToUpperInvariant();
            string buildingName = dto.Name.Trim();
            string? description = NormalizeOptionalValue(dto.Description);

            if (buildingId.Length > 50)
                return BadRequest(new { message = "Mã tòa nhà không được vượt quá 50 ký tự." });

            if (buildingName.Length > 255)
                return BadRequest(new { message = "Tên tòa nhà không được vượt quá 255 ký tự." });

            if (description?.Length > 255)
                return BadRequest(new { message = "Mô tả không được vượt quá 255 ký tự." });

            if (dto.Floors <= 0 || dto.Floors > 100)
                return BadRequest(new { message = "Số tầng phải nằm trong khoảng từ 1 đến 100." });

            bool buildingExists = await _context.toa_nha
                .AsNoTracking()
                .AnyAsync(building => building.ma_toa_nha == buildingId);

            if (buildingExists)
                return Conflict(new { message = "Mã tòa nhà đã tồn tại." });

            var building = new ToaNha
            {
                ma_toa_nha = buildingId,
                ten_toa_nha = buildingName,
                so_tang = dto.Floors,
                mo_ta = description
            };

            _context.toa_nha.Add(building);
            AddAuditLog("TAO_TOA_NHA", $"Tạo tòa nhà {buildingId} - {buildingName}.");

            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException ex)
            {
                _logger.LogWarning(ex, "Trùng dữ liệu khi tạo tòa nhà {BuildingId}.", buildingId);
                return Conflict(new { message = "Mã tòa nhà đã tồn tại." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Không thể tạo tòa nhà {BuildingId}.", buildingId);
                return StatusCode(StatusCodes.Status500InternalServerError, new { message = "Không thể tạo tòa nhà." });
            }

            return StatusCode(StatusCodes.Status201Created, new
            {
                message = $"Đã thêm tòa nhà {buildingName}."
            });
        }

        [HttpPut("buildings/{id}")]
        public async Task<IActionResult> UpdateBuilding(string id, [FromBody] UpdateBuildingDTO dto)
        {
            if (string.IsNullOrWhiteSpace(id) || dto == null)
                return BadRequest(new { message = "Dữ liệu tòa nhà không hợp lệ." });

            string buildingId = id.Trim().ToUpperInvariant();

            var building = await _context.toa_nha.FindAsync(buildingId);
            if (building == null) return NotFound(new { message = "Không tìm thấy tòa nhà." });

            if (string.IsNullOrWhiteSpace(dto.Name))
                return BadRequest(new { message = "Tên tòa nhà không được để trống." });

            string buildingName = dto.Name.Trim();
            string? description = NormalizeOptionalValue(dto.Description);

            if (buildingName.Length > 255)
                return BadRequest(new { message = "Tên tòa nhà không được vượt quá 255 ký tự." });

            if (description?.Length > 255)
                return BadRequest(new { message = "Mô tả không được vượt quá 255 ký tự." });

            if (dto.Floors <= 0 || dto.Floors > 100)
                return BadRequest(new { message = "Số tầng phải nằm trong khoảng từ 1 đến 100." });

            int highestRoomFloor = await _context.phong_hoc
                .Where(room => room.ma_toa_nha == buildingId)
                .Select(room => (int?)room.tang)
                .MaxAsync() ?? 0;

            if (dto.Floors < highestRoomFloor)
            {
                return BadRequest(new
                {
                    message = $"Không thể giảm xuống {dto.Floors} tầng vì đang có phòng ở tầng {highestRoomFloor}."
                });
            }

            building.ten_toa_nha = buildingName;
            building.so_tang = dto.Floors;
            building.mo_ta = description;

            AddAuditLog("CAP_NHAT_TOA_NHA", $"Cập nhật tòa nhà {buildingId}.");

            try
            {
                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Không thể cập nhật tòa nhà {BuildingId}.", buildingId);
                return StatusCode(StatusCodes.Status500InternalServerError, new { message = "Không thể cập nhật tòa nhà." });
            }

            return Ok(new { message = "Cập nhật tòa nhà thành công." });
        }

        [HttpDelete("buildings/{id}")]
        public async Task<IActionResult> DeleteBuilding(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return BadRequest(new { message = "Mã tòa nhà không hợp lệ." });

            string buildingId = id.Trim().ToUpperInvariant();

            var building = await _context.toa_nha.FindAsync(buildingId);
            if (building == null) return NotFound(new { message = "Không tìm thấy tòa nhà." });

            bool hasRooms = await _context.phong_hoc
                .AsNoTracking()
                .AnyAsync(room => room.ma_toa_nha == buildingId);

            if (hasRooms)
                return BadRequest(new { message = "Không thể xóa tòa nhà đang có phòng học trực thuộc." });

            _context.toa_nha.Remove(building);
            AddAuditLog("XOA_TOA_NHA", $"Xóa tòa nhà {buildingId} - {building.ten_toa_nha}.");

            try
            {
                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Không thể xóa tòa nhà {BuildingId}.", buildingId);
                return StatusCode(StatusCodes.Status500InternalServerError, new { message = "Không thể xóa tòa nhà." });
            }

            return Ok(new { message = "Đã xóa tòa nhà." });
        }

        [HttpGet("rooms")]
        public async Task<IActionResult> GetAllRooms()
        {
            DateTime today = DateTime.Today;
            DateTime tomorrow = today.AddDays(1);

            var activeSessions = await _context.phieu_muon
                .AsNoTracking()
                .Where(session => ActiveStatuses.Contains(session.trang_thai))
                .Select(session => new
                {
                    session.ma_phong,
                    session.trang_thai,
                    session.ngay_muon,
                    session.thoi_gian_tao
                })
                .ToListAsync();

            var currentSessions = activeSessions
                .Where(session => session.trang_thai != "PENDING"
                    || (session.ngay_muon >= today && session.ngay_muon < tomorrow))
                .GroupBy(session => session.ma_phong)
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderByDescending(session => session.thoi_gian_tao).First());

            var cabinets = await _context.thiet_bi_iot
                .AsNoTracking()
                .Where(cabinet => cabinet.ma_phong != null)
                .ToDictionaryAsync(cabinet => cabinet.ma_phong!);

            var rooms = await _context.phong_hoc
                .AsNoTracking()
                .Include(room => room.MaToaNhaNavigation)
                .OrderBy(room => room.ma_toa_nha)
                .ThenBy(room => room.tang)
                .ThenBy(room => room.ma_phong)
                .ToListAsync();

            var result = rooms.Select(room =>
            {
                currentSessions.TryGetValue(room.ma_phong, out var currentSession);
                cabinets.TryGetValue(room.ma_phong, out var cabinet);

                return new
                {
                    id = room.ma_phong,
                    name = room.ten_phong,
                    buildingId = room.ma_toa_nha,
                    building = room.MaToaNhaNavigation?.ten_toa_nha ?? room.ma_toa_nha,
                    floor = $"Tầng {room.tang}",
                    floorNum = room.tang,
                    capacity = room.suc_chua ?? 0,
                    rawStatus = room.trang_thai,
                    status = GetRoomDisplayStatus(room.trang_thai, cabinet, currentSession?.trang_thai),
                    hasCabinet = cabinet != null,
                    isOnline = cabinet?.trang_thai_mang ?? false
                };
            });

            return Ok(result);
        }

        [HttpPost("rooms")]
        public async Task<IActionResult> CreateRoom([FromBody] CreateRoomDTO dto)
        {
            if (dto == null
                || string.IsNullOrWhiteSpace(dto.Id)
                || string.IsNullOrWhiteSpace(dto.Name)
                || string.IsNullOrWhiteSpace(dto.BuildingId))
            {
                return BadRequest(new { message = "Vui lòng nhập mã phòng, tên phòng và tòa nhà." });
            }

            string roomId = dto.Id.Trim().ToUpperInvariant();
            string roomName = dto.Name.Trim();
            string buildingId = dto.BuildingId.Trim().ToUpperInvariant();
            string? roomStatus = NormalizeRoomStatus(dto.Status);

            if (roomId.Length > 50)
                return BadRequest(new { message = "Mã phòng không được vượt quá 50 ký tự." });

            if (roomName.Length > 255)
                return BadRequest(new { message = "Tên phòng không được vượt quá 255 ký tự." });

            if (roomStatus == null)
                return BadRequest(new { message = "Trạng thái phòng không hợp lệ." });

            if (dto.Capacity <= 0 || dto.Capacity > 1000)
                return BadRequest(new { message = "Sức chứa phải nằm trong khoảng từ 1 đến 1000." });

            var building = await _context.toa_nha
                .AsNoTracking()
                .FirstOrDefaultAsync(item => item.ma_toa_nha == buildingId);

            if (building == null) return BadRequest(new { message = "Tòa nhà không tồn tại." });

            int buildingFloors = building.so_tang ?? 1;

            if (dto.Floor <= 0 || dto.Floor > buildingFloors)
                return BadRequest(new { message = $"Tầng phải nằm trong khoảng từ 1 đến {buildingFloors}." });

            bool roomExists = await _context.phong_hoc
                .AsNoTracking()
                .AnyAsync(room => room.ma_phong == roomId);

            if (roomExists)
                return Conflict(new { message = "Mã phòng đã tồn tại." });

            var room = new PhongHoc
            {
                ma_phong = roomId,
                ten_phong = roomName,
                tang = dto.Floor,
                suc_chua = dto.Capacity,
                ma_toa_nha = buildingId,
                trang_thai = roomStatus
            };

            _context.phong_hoc.Add(room);
            AddAuditLog("TAO_PHONG_HOC", $"Tạo phòng {roomId} tại tòa nhà {buildingId}.");

            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException ex)
            {
                _logger.LogWarning(ex, "Trùng dữ liệu khi tạo phòng {RoomId}.", roomId);
                return Conflict(new { message = "Mã phòng đã tồn tại." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Không thể tạo phòng {RoomId}.", roomId);
                return StatusCode(StatusCodes.Status500InternalServerError, new { message = "Không thể tạo phòng học." });
            }

            return StatusCode(StatusCodes.Status201Created, new
            {
                message = $"Đã thêm phòng {roomName}. Hãy đăng ký tủ IoT vật lý cho phòng này.",
                roomId
            });
        }

        [HttpPut("rooms/{id}")]
        public async Task<IActionResult> UpdateRoom(string id, [FromBody] UpdateRoomDTO dto)
        {
            if (string.IsNullOrWhiteSpace(id) || dto == null)
                return BadRequest(new { message = "Dữ liệu phòng học không hợp lệ." });

            string roomId = id.Trim().ToUpperInvariant();

            var room = await _context.phong_hoc.FindAsync(roomId);
            if (room == null) return NotFound(new { message = "Không tìm thấy phòng học." });

            if (string.IsNullOrWhiteSpace(dto.Name) || string.IsNullOrWhiteSpace(dto.BuildingId))
                return BadRequest(new { message = "Tên phòng và tòa nhà không được để trống." });

            string roomName = dto.Name.Trim();
            string buildingId = dto.BuildingId.Trim().ToUpperInvariant();
            string? roomStatus = NormalizeRoomStatus(dto.Status ?? room.trang_thai);

            if (roomName.Length > 255)
                return BadRequest(new { message = "Tên phòng không được vượt quá 255 ký tự." });

            if (roomStatus == null)
                return BadRequest(new { message = "Trạng thái phòng không hợp lệ." });

            if (dto.Capacity <= 0 || dto.Capacity > 1000)
                return BadRequest(new { message = "Sức chứa phải nằm trong khoảng từ 1 đến 1000." });

            var building = await _context.toa_nha
                .AsNoTracking()
                .FirstOrDefaultAsync(item => item.ma_toa_nha == buildingId);

            if (building == null) return BadRequest(new { message = "Tòa nhà không tồn tại." });

            int buildingFloors = building.so_tang ?? 1;

            if (dto.Floor <= 0 || dto.Floor > buildingFloors)
                return BadRequest(new { message = $"Tầng phải nằm trong khoảng từ 1 đến {buildingFloors}." });

            room.ten_phong = roomName;
            room.tang = dto.Floor;
            room.suc_chua = dto.Capacity;
            room.ma_toa_nha = buildingId;
            room.trang_thai = roomStatus;

            AddAuditLog("CAP_NHAT_PHONG_HOC", $"Cập nhật phòng {roomId}.");

            try
            {
                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Không thể cập nhật phòng {RoomId}.", roomId);
                return StatusCode(StatusCodes.Status500InternalServerError, new { message = "Không thể cập nhật phòng học." });
            }

            return Ok(new { message = "Cập nhật phòng học thành công." });
        }

        [HttpDelete("rooms/{id}")]
        public async Task<IActionResult> DeleteRoom(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return BadRequest(new { message = "Mã phòng không hợp lệ." });

            string roomId = id.Trim().ToUpperInvariant();

            var room = await _context.phong_hoc.FindAsync(roomId);
            if (room == null) return NotFound(new { message = "Không tìm thấy phòng học." });

            bool hasActiveSession = await _context.phieu_muon
                .AsNoTracking()
                .AnyAsync(session => session.ma_phong == roomId && ActiveStatuses.Contains(session.trang_thai));

            if (hasActiveSession)
                return BadRequest(new { message = "Không thể vô hiệu hóa phòng đang có phiên mượn chưa hoàn tất." });

            if (room.trang_thai == "TAM_KHOA")
                return Ok(new { message = "Phòng đã được vô hiệu hóa từ trước." });

            room.trang_thai = "TAM_KHOA";
            AddAuditLog("VO_HIEU_HOA_PHONG_HOC", $"Vô hiệu hóa phòng {roomId}. Dữ liệu lịch sử được giữ lại.");

            try
            {
                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Không thể vô hiệu hóa phòng {RoomId}.", roomId);

                return StatusCode(
                    StatusCodes.Status500InternalServerError,
                    new { message = "Không thể vô hiệu hóa phòng học." });
            }

            return Ok(new
            {
                message = "Đã vô hiệu hóa phòng học. Dữ liệu tủ và lịch sử vẫn được giữ lại.",
                status = "TAM_KHOA"
            });
        }

        private void AddAuditLog(string action, string detail)
        {
            string? adminId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            _context.nhat_ky_he_thong.Add(new NhatKyHeThong
            {
                ma_sv = adminId,
                hanh_dong = action,
                chi_tiet = detail,
                thoi_gian = DateTime.Now,
                ip_address = Helper.GetClientIp(HttpContext),
                user_agent = Helper.GetClientOs(Request)
            });
        }

        private static string? NormalizeRoomStatus(string? status)
        {
            if (string.IsNullOrWhiteSpace(status)) return "HOAT_DONG";

            string normalizedStatus = status.Trim().ToUpperInvariant();

            return ValidRoomStatuses.Contains(normalizedStatus)
                ? normalizedStatus
                : null;
        }

        private static string GetRoomDisplayStatus(
            string roomStatus,
            ThietBiIot? cabinet,
            string? borrowingStatus)
        {
            if (roomStatus == "BAO_TRI") return "Bảo trì";
            if (roomStatus == "TAM_KHOA") return "Tạm khóa";
            if (cabinet == null) return "Chưa có tủ IoT";
            if (!cabinet.trang_thai_mang) return "Mất kết nối";
            if (borrowingStatus == "RETURNING") return "Đang xác nhận trả";
            if (borrowingStatus is "ACTIVE" or "IN_USE") return "Đang mượn";
            if (borrowingStatus == "PENDING") return "Đã đặt";

            return "Sẵn sàng";
        }

        private static string? NormalizeOptionalValue(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }
}