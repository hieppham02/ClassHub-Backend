using BCryptNet = BCrypt.Net.BCrypt;
using ClassHub_API.Data;
using ClassHub_API.DTOs;
using ClassHub_API.Models;
using ClassHub_API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Security.Cryptography;

namespace ClassHub_API.Controllers
{
    [Route("api/admin/accounts")]
    [ApiController]
    [Authorize(Roles = "ADMIN")]
    public class AdminAccountController : ControllerBase
    {
        private const int BcryptWorkFactor = 11;
        private static readonly string[] ActiveStatuses = { "PENDING", "ACTIVE", "IN_USE", "RETURNING" };

        private readonly AppDbContext _context;
        private readonly ILogger<AdminAccountController> _logger;

        public AdminAccountController(AppDbContext context, ILogger<AdminAccountController> logger)
        {
            _context = context;
            _logger = logger;
        }

        [HttpGet]
        public async Task<IActionResult> GetAllAccounts()
        {
            var rawAccounts = await _context.tai_khoan
                .AsNoTracking()
                .OrderByDescending(a => a.ngay_tao)
                .Select(a => new
                {
                    a.ma_sv,
                    a.ho_ten,
                    a.email,
                    a.sdt,
                    a.vai_tro,
                    a.ten_lop,
                    a.trang_thai,
                    a.ngay_tao
                })
                .ToListAsync();

            var accounts = rawAccounts.Select(a => new
            {
                id = a.ma_sv,
                name = a.ho_ten,
                email = a.email,
                phone = a.sdt ?? "---",
                role = GetRoleLabel(a.vai_tro),
                rawRole = a.vai_tro,
                className = a.ten_lop ?? "---",
                isActive = a.trang_thai,
                createdAt = a.ngay_tao.ToString("dd/MM/yyyy")
            });

            return Ok(accounts);
        }

        [HttpPost]
        public async Task<IActionResult> CreateAccount([FromBody] CreateAccountDTO dto)
        {
            if (dto == null)
                return BadRequest(new { message = "Dữ liệu tài khoản không hợp lệ." });

            if (string.IsNullOrWhiteSpace(dto.Id)
                || string.IsNullOrWhiteSpace(dto.Name)
                || string.IsNullOrWhiteSpace(dto.Email))
            {
                return BadRequest(new { message = "Vui lòng nhập mã tài khoản, họ tên và email." });
            }

            string accountId = dto.Id.Trim().ToUpperInvariant();
            string fullName = dto.Name.Trim();
            string email = dto.Email.Trim().ToLowerInvariant();
            string? phone = NormalizeOptionalValue(dto.Phone);
            string? classOrDepartment = NormalizeOptionalValue(dto.ClassOrDept);
            string? role = NormalizeRole(dto.Role);

            if (accountId.Length > 50)
                return BadRequest(new { message = "Mã tài khoản không được vượt quá 50 ký tự." });

            if (fullName.Length > 100)
                return BadRequest(new { message = "Họ tên không được vượt quá 100 ký tự." });

            if (email.Length > 100 || !new EmailAddressAttribute().IsValid(email))
                return BadRequest(new { message = "Email không hợp lệ." });

            if (phone?.Length > 15)
                return BadRequest(new { message = "Số điện thoại không được vượt quá 15 ký tự." });

            if (classOrDepartment?.Length > 50)
                return BadRequest(new { message = "Tên lớp hoặc phòng ban không được vượt quá 50 ký tự." });

            if (role == null)
                return BadRequest(new { message = "Vai trò tài khoản không hợp lệ." });

            bool accountExists = await _context.tai_khoan
                .AsNoTracking()
                .AnyAsync(a => a.ma_sv == accountId || a.email == email);

            if (accountExists)
                return Conflict(new { message = "Mã tài khoản hoặc email đã tồn tại." });

            string? temporaryPassword = null;
            string rawPassword;

            if (string.IsNullOrWhiteSpace(dto.Password))
            {
                temporaryPassword = GenerateTemporaryPassword();
                rawPassword = temporaryPassword;
            }
            else
            {
                rawPassword = dto.Password;
            }

            if (rawPassword.Length < 6 || rawPassword.Length > 72)
                return BadRequest(new { message = "Mật khẩu phải có từ 6 ký tự trở lên" });

            var newAccount = new TaiKhoan
            {
                ma_sv = accountId,
                ho_ten = fullName,
                email = email,
                mat_khau = BCryptNet.HashPassword(rawPassword, BcryptWorkFactor),
                sdt = phone,
                vai_tro = role,
                ten_lop = role == "ADMIN" ? "Ban Quản Trị" : classOrDepartment,
                trang_thai = true,
                ngay_tao = DateTime.Now
            };

            _context.tai_khoan.Add(newAccount);

            AddAuditLog(
                "TAO_TAI_KHOAN",
                $"Tạo tài khoản {accountId} với vai trò {role}.");

            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException ex)
            {
                _logger.LogWarning(ex, "Trùng dữ liệu khi tạo tài khoản {AccountId}.", accountId);
                return Conflict(new { message = "Mã tài khoản hoặc email đã tồn tại." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Không thể tạo tài khoản {AccountId}.", accountId);
                return StatusCode(StatusCodes.Status500InternalServerError, new { message = "Không thể tạo tài khoản." });
            }

            if (temporaryPassword != null)
            {
                return StatusCode(StatusCodes.Status201Created, new
                {
                    message = $"Đã tạo tài khoản {newAccount.ho_ten}. Mật khẩu tạm thời: {temporaryPassword}",
                    temporaryPassword
                });
            }

            return StatusCode(StatusCodes.Status201Created, new
            {
                message = $"Đã tạo tài khoản cho {newAccount.ho_ten}."
            });
        }

        [HttpPut("{id}")]
        public async Task<IActionResult> UpdateAccount(string id, [FromBody] UpdateAccountDTO dto)
        {
            if (string.IsNullOrWhiteSpace(id) || dto == null)
                return BadRequest(new { message = "Dữ liệu tài khoản không hợp lệ." });

            string accountId = id.Trim().ToUpperInvariant();

            var user = await _context.tai_khoan.FindAsync(accountId);
            if (user == null) return NotFound(new { message = "Không tìm thấy tài khoản." });

            if (string.IsNullOrWhiteSpace(dto.Name) || string.IsNullOrWhiteSpace(dto.Email))
                return BadRequest(new { message = "Họ tên và email không được để trống." });

            string fullName = dto.Name.Trim();
            string email = dto.Email.Trim().ToLowerInvariant();
            string? phone = NormalizeOptionalValue(dto.Phone);
            string? classOrDepartment = NormalizeOptionalValue(dto.ClassOrDept);
            string? role = NormalizeRole(dto.Role);

            if (fullName.Length > 100)
                return BadRequest(new { message = "Họ tên không được vượt quá 100 ký tự." });

            if (email.Length > 100 || !new EmailAddressAttribute().IsValid(email))
                return BadRequest(new { message = "Email không hợp lệ." });

            if (phone?.Length > 15)
                return BadRequest(new { message = "Số điện thoại không được vượt quá 15 ký tự." });

            if (classOrDepartment?.Length > 50)
                return BadRequest(new { message = "Tên lớp hoặc phòng ban không được vượt quá 50 ký tự." });

            if (role == null)
                return BadRequest(new { message = "Vai trò tài khoản không hợp lệ." });

            string? currentAdminId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            if (accountId == currentAdminId && role != "ADMIN")
                return BadRequest(new { message = "Bạn không thể tự xóa quyền quản trị của mình." });

            bool emailExists = await _context.tai_khoan
                .AsNoTracking()
                .AnyAsync(a => a.email == email && a.ma_sv != accountId);

            if (emailExists)
                return Conflict(new { message = "Email đã được sử dụng bởi tài khoản khác." });

            if (!string.IsNullOrWhiteSpace(dto.NewPassword)
                && (dto.NewPassword.Length < 8 || dto.NewPassword.Length > 72))
            {
                return BadRequest(new { message = "Mật khẩu mới phải có từ 8 đến 72 ký tự." });
            }

            user.ho_ten = fullName;
            user.email = email;
            user.sdt = phone;
            user.vai_tro = role;
            user.ten_lop = role == "ADMIN" ? "Ban Quản Trị" : classOrDepartment;

            if (!string.IsNullOrWhiteSpace(dto.NewPassword))
            {
                user.mat_khau = BCryptNet.HashPassword(dto.NewPassword, BcryptWorkFactor);
            }

            AddAuditLog(
                "CAP_NHAT_TAI_KHOAN",
                $"Cập nhật tài khoản {accountId}, vai trò {role}.");

            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException ex)
            {
                _logger.LogWarning(ex, "Trùng dữ liệu khi cập nhật tài khoản {AccountId}.", accountId);
                return Conflict(new { message = "Email đã được sử dụng bởi tài khoản khác." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Không thể cập nhật tài khoản {AccountId}.", accountId);
                return StatusCode(StatusCodes.Status500InternalServerError, new { message = "Không thể cập nhật tài khoản." });
            }

            return Ok(new { message = "Cập nhật tài khoản thành công." });
        }

        [HttpPatch("{id}/toggle-status")]
        public async Task<IActionResult> ToggleAccountStatus(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return BadRequest(new { message = "Mã tài khoản không hợp lệ." });

            string accountId = id.Trim().ToUpperInvariant();

            var user = await _context.tai_khoan.FindAsync(accountId);
            if (user == null) return NotFound(new { message = "Không tìm thấy tài khoản." });

            string? currentAdminId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            if (accountId == currentAdminId)
                return BadRequest(new { message = "Bạn không thể tự khóa tài khoản của mình." });

            if (user.trang_thai)
            {
                bool hasActiveSession = await _context.phieu_muon
                    .AsNoTracking()
                    .AnyAsync(p => p.ma_sv == accountId && ActiveStatuses.Contains(p.trang_thai));

                if (hasActiveSession)
                    return BadRequest(new { message = "Không thể khóa tài khoản đang có phiên mượn chưa hoàn tất." });
            }

            user.trang_thai = !user.trang_thai;

            AddAuditLog(
                user.trang_thai ? "MO_KHOA_TAI_KHOAN" : LogAction.KHOA_TAI_KHOAN.ToString(),
                $"{(user.trang_thai ? "Mở khóa" : "Khóa")} tài khoản {accountId}.");

            try
            {
                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Không thể đổi trạng thái tài khoản {AccountId}.", accountId);
                return StatusCode(StatusCodes.Status500InternalServerError, new { message = "Không thể đổi trạng thái tài khoản." });
            }

            string message = user.trang_thai ? "Mở khóa tài khoản thành công." : "Khóa tài khoản thành công.";
            return Ok(new { message, isActive = user.trang_thai });
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteAccount(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return BadRequest(new { message = "Mã tài khoản không hợp lệ." });

            string accountId = id.Trim().ToUpperInvariant();

            var user = await _context.tai_khoan.FindAsync(accountId);
            if (user == null) return NotFound(new { message = "Không tìm thấy tài khoản." });

            string? currentAdminId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            if (accountId == currentAdminId)
                return BadRequest(new { message = "Bạn không thể tự vô hiệu hóa tài khoản của mình." });

            bool hasActiveSession = await _context.phieu_muon
                .AsNoTracking()
                .AnyAsync(p => p.ma_sv == accountId && ActiveStatuses.Contains(p.trang_thai));

            if (hasActiveSession)
                return BadRequest(new { message = "Không thể vô hiệu hóa tài khoản đang có phiên mượn chưa hoàn tất." });

            if (!user.trang_thai)
                return Ok(new { message = "Tài khoản đã được vô hiệu hóa từ trước." });

            user.trang_thai = false;

            AddAuditLog(
                "VO_HIEU_HOA_TAI_KHOAN",
                $"Vô hiệu hóa tài khoản {accountId}. Dữ liệu lịch sử được giữ lại.");

            try
            {
                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Không thể vô hiệu hóa tài khoản {AccountId}.", accountId);

                return StatusCode(
                    StatusCodes.Status500InternalServerError,
                    new { message = "Không thể vô hiệu hóa tài khoản." });
            }

            return Ok(new
            {
                message = "Đã vô hiệu hóa tài khoản. Dữ liệu lịch sử vẫn được giữ lại.",
                isActive = false
            });
        }

        private void AddAuditLog(string action, string detail)
        {
            string? currentAdminId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            _context.nhat_ky_he_thong.Add(new NhatKyHeThong
            {
                ma_sv = currentAdminId,
                hanh_dong = action,
                chi_tiet = detail,
                thoi_gian = DateTime.Now,
                ip_address = Helper.GetClientIp(HttpContext),
                user_agent = Helper.GetClientOs(Request)
            });
        }

        private static string? NormalizeRole(string? role)
        {
            if (string.IsNullOrWhiteSpace(role)) return null;

            return role.Trim().ToUpperInvariant() switch
            {
                "ADMIN" => "ADMIN",
                "QUẢN TRỊ VIÊN" => "ADMIN",
                "GIẢNG VIÊN" => "GIANGVIEN",
                "GIANGVIEN" => "GIANGVIEN",
                "SINH VIÊN" => "SINHVIEN",
                "SINHVIEN" => "SINHVIEN",
                _ => null
            };
        }

        private static string GetRoleLabel(string role)
        {
            return role switch
            {
                "ADMIN" => "Admin",
                "GIANGVIEN" => "Giảng viên",
                _ => "Sinh viên"
            };
        }

        private static string? NormalizeOptionalValue(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static string GenerateTemporaryPassword()
        {
            return Convert.ToBase64String(RandomNumberGenerator.GetBytes(12));
        }
    }
}