using BCryptNet = BCrypt.Net.BCrypt;
using ClassHub_API.Data;
using ClassHub_API.DTOs;
using ClassHub_API.Interfaces;
using ClassHub_API.Models;
using ClassHub_API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;

namespace ClassHub_API.Controllers
{
    [Route("api/auth")]
    [ApiController]
    public class AuthController : ControllerBase
    {
        private const int BcryptWorkFactor = 11;

        private readonly AppDbContext _context;
        private readonly ITokenService _tokenService;
        private readonly ILogger<AuthController> _logger;

        public AuthController(AppDbContext context, ITokenService tokenService, ILogger<AuthController> logger)
        {
            _context = context;
            _tokenService = tokenService;
            _logger = logger;
        }

        [AllowAnonymous]
        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginDTO model)
        {
            if (model == null || string.IsNullOrWhiteSpace(model.MaSV) || string.IsNullOrWhiteSpace(model.MatKhau))
                return BadRequest(new { message = "Vui lòng nhập đầy đủ tài khoản và mật khẩu." });

            string identifier = model.MaSV.Trim();
            string normalizedStudentId = identifier.ToUpperInvariant();
            string normalizedEmail = identifier.ToLowerInvariant();

            var user = await _context.tai_khoan
                .FirstOrDefaultAsync(u => u.ma_sv == normalizedStudentId || u.email == normalizedEmail);

            if (user == null)
                return Unauthorized(new { message = "Tài khoản hoặc mật khẩu không chính xác." });

            bool isHashedPassword = IsBcryptHash(user.mat_khau);
            bool isPasswordValid;

            try
            {
                isPasswordValid = isHashedPassword
                    ? BCryptNet.Verify(model.MatKhau, user.mat_khau)
                    : VerifyLegacyPassword(model.MatKhau, user.mat_khau);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Mật khẩu của tài khoản {UserId} có định dạng không hợp lệ.", user.ma_sv);
                return Unauthorized(new { message = "Tài khoản hoặc mật khẩu không chính xác." });
            }

            if (!isPasswordValid)
                return Unauthorized(new { message = "Tài khoản hoặc mật khẩu không chính xác." });

            if (!user.trang_thai)
            {
                return StatusCode(StatusCodes.Status403Forbidden, new
                {
                    message = "Tài khoản đã bị khóa. Vui lòng liên hệ quản trị viên."
                });
            }

            if (!isHashedPassword || BCryptNet.PasswordNeedsRehash(user.mat_khau, BcryptWorkFactor))
            {
                user.mat_khau = BCryptNet.HashPassword(model.MatKhau, BcryptWorkFactor);
            }

            _context.nhat_ky_he_thong.Add(new NhatKyHeThong
            {
                ma_sv = user.ma_sv,
                hanh_dong = LogAction.DANG_NHAP.ToString(),
                chi_tiet = $"{user.vai_tro} đăng nhập thành công.",
                thoi_gian = DateTime.Now,
                ip_address = Helper.GetClientIp(HttpContext),
                user_agent = Helper.GetClientOs(Request)
            });

            try
            {
                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Không thể lưu lịch sử đăng nhập của tài khoản {UserId}.", user.ma_sv);
            }

            string token = _tokenService.GenerateToken(user);

            return Ok(new
            {
                message = "Đăng nhập thành công.",
                token,
                user = new
                {
                    ma_sv = user.ma_sv,
                    name = user.ho_ten,
                    email = user.email,
                    role = user.vai_tro,
                    ten_lop = user.ten_lop
                }
            });
        }

        [AllowAnonymous]
        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody] RegisterDTO model)
        {
            if (model == null)
                return BadRequest(new { message = "Dữ liệu đăng ký không hợp lệ." });

            if (string.IsNullOrWhiteSpace(model.MaSV)
                || string.IsNullOrWhiteSpace(model.HoTen)
                || string.IsNullOrWhiteSpace(model.Email)
                || string.IsNullOrWhiteSpace(model.MatKhau))
            {
                return BadRequest(new { message = "Vui lòng nhập đầy đủ thông tin bắt buộc." });
            }

            string studentId = model.MaSV.Trim().ToUpperInvariant();
            string fullName = model.HoTen.Trim();
            string email = model.Email.Trim().ToLowerInvariant();
            string? phone = NormalizeOptionalValue(model.Sdt);
            string? className = NormalizeOptionalValue(model.TenLop);

            if (studentId.Length > 50)
                return BadRequest(new { message = "Mã sinh viên không được vượt quá 50 ký tự." });

            if (fullName.Length > 100)
                return BadRequest(new { message = "Họ tên không được vượt quá 100 ký tự." });

            if (email.Length > 100 || !new EmailAddressAttribute().IsValid(email))
                return BadRequest(new { message = "Email không hợp lệ." });

            if (model.MatKhau.Length < 8 || model.MatKhau.Length > 72)
                return BadRequest(new { message = "Mật khẩu phải có từ 8 đến 72 ký tự." });

            if (phone?.Length > 15)
                return BadRequest(new { message = "Số điện thoại không được vượt quá 15 ký tự." });

            if (className?.Length > 50)
                return BadRequest(new { message = "Tên lớp không được vượt quá 50 ký tự." });

            bool accountExists = await _context.tai_khoan
                .AsNoTracking()
                .AnyAsync(u => u.ma_sv == studentId || u.email == email);

            if (accountExists)
                return Conflict(new { message = "Mã sinh viên hoặc email đã được đăng ký." });

            var newUser = new TaiKhoan
            {
                ma_sv = studentId,
                ho_ten = fullName,
                email = email,
                mat_khau = BCryptNet.HashPassword(model.MatKhau, BcryptWorkFactor),
                sdt = phone,
                ten_lop = className,
                vai_tro = "SINHVIEN",
                trang_thai = true,
                ngay_tao = DateTime.Now
            };

            _context.tai_khoan.Add(newUser);

            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException ex)
            {
                _logger.LogWarning(ex, "Trùng dữ liệu khi đăng ký tài khoản {UserId}.", studentId);
                return Conflict(new { message = "Mã sinh viên hoặc email đã được đăng ký." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Không thể đăng ký tài khoản {UserId}.", studentId);
                return StatusCode(StatusCodes.Status500InternalServerError, new { message = "Không thể đăng ký tài khoản." });
            }

            return StatusCode(StatusCodes.Status201Created, new
            {
                message = "Đăng ký tài khoản thành công. Vui lòng đăng nhập."
            });
        }

        private static bool IsBcryptHash(string password)
        {
            return password.StartsWith("$2a$")
                || password.StartsWith("$2b$")
                || password.StartsWith("$2y$");
        }

        private static bool VerifyLegacyPassword(string providedPassword, string storedPassword)
        {
            byte[] providedBytes = Encoding.UTF8.GetBytes(providedPassword);
            byte[] storedBytes = Encoding.UTF8.GetBytes(storedPassword);

            return CryptographicOperations.FixedTimeEquals(providedBytes, storedBytes);
        }

        private static string? NormalizeOptionalValue(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }
}