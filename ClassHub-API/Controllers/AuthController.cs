using ClassHub_API.Data;
using ClassHub_API.DTOs;
using ClassHub_API.Interfaces;
using ClassHub_API.Models;
using ClassHub_API.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static ClassHub_API.Services.Helper;
using BCryptNet = BCrypt.Net.BCrypt;

namespace ClassHub_API.Controllers
{
    [Route("api/auth")]
    [ApiController]
    public class AuthController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly ITokenService _tokenService;

        public AuthController(AppDbContext context, ITokenService tokenService)
        {
            _context = context;
            _tokenService = tokenService;
        }

        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginDTO model)
        {
            if (string.IsNullOrWhiteSpace(model.MaSV) || string.IsNullOrWhiteSpace(model.MatKhau))
            {
                return BadRequest(new { message = "Vui lòng nhập đầy đủ tài khoản và mật khẩu!" });
            }

            var user = await _context.tai_khoan.FirstOrDefaultAsync(u => u.ma_sv == model.MaSV.Trim() || u.email == model.MaSV.Trim());

            if (user == null)
            {
                return Unauthorized(new { message = "Tài khoản hoặc mật khẩu không chính xác!" });
            }

            if (user.trang_thai == false)
            {
                return StatusCode(StatusCodes.Status403Forbidden, new
                {
                    message = "Tài khoản của bạn đã bị tạm khóa do vi phạm quy chế. Vui lòng liên hệ Quản trị viên!"
                });
            }

            bool isPasswordValid = false;
            bool isHashed = user.mat_khau.StartsWith("$2a$") ||
                            user.mat_khau.StartsWith("$2b$") ||
                            user.mat_khau.StartsWith("$2y$");

            if (isHashed)
            {
                isPasswordValid = BCryptNet.Verify(model.MatKhau, user.mat_khau);
            }
            else
            {
                isPasswordValid = (user.mat_khau == model.MatKhau);

                if (isPasswordValid)
                {
                    user.mat_khau = BCryptNet.HashPassword(model.MatKhau, workFactor: 11);
                    await _context.SaveChangesAsync();
                }
            }

            if (!isPasswordValid)
            {
                return Unauthorized(new { message = "Tài khoản hoặc mật khẩu không chính xác!" });
            }

            try
            {
                var log = new NhatKyHeThong
                {
                    ma_sv = user.ma_sv,
                    hanh_dong = LogAction.DANG_NHAP.ToString(),
                    chi_tiet = $"{user.vai_tro} Đăng nhập thành công ",
                    thoi_gian = DateTime.Now,
                    ip_address = Helper.GetClientIp(HttpContext),
                    user_agent = Helper.GetClientOs(Request),
                };
                _context.nhat_ky_he_thong.Add(log);
                await _context.SaveChangesAsync();
            }
            catch
            {
            }

            var tokenString = _tokenService.GenerateToken(user);

            return Ok(new
            {
                message = "Đăng nhập thành công!",
                token = tokenString,
                user = new
                {
                    ma_sv = user.ma_sv,
                    name = user.ho_ten,
                    email = user.email,
                    role = user.vai_tro,
                    ten_lop = user.ten_lop,
                }
            });
        }

        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody] RegisterDTO model)
        {
            if (string.IsNullOrWhiteSpace(model.MaSV) || string.IsNullOrWhiteSpace(model.Email) || string.IsNullOrWhiteSpace(model.MatKhau))
            {
                return BadRequest(new { message = "Vui lòng nhập đầy đủ thông tin bắt buộc!" });
            }

            var existingUser = await _context.tai_khoan
                .FirstOrDefaultAsync(u => u.ma_sv == model.MaSV.Trim() || u.email == model.Email.Trim());

            if (existingUser != null)
            {
                return BadRequest(new { message = "Mã sinh viên hoặc Email đã được đăng ký trên hệ thống!" });
            }

            string hashedPassword = BCryptNet.HashPassword(model.MatKhau, workFactor: 11);

            var newUser = new TaiKhoan
            {
                ma_sv = model.MaSV.Trim(),
                ho_ten = model.HoTen.Trim(),
                email = model.Email.Trim(),
                mat_khau = hashedPassword,
                sdt = model.Sdt?.Trim(),
                ten_lop = model.TenLop?.Trim(),
                vai_tro = "SINHVIEN",
                trang_thai = true,
                ngay_tao = DateTime.Now
            };

            _context.tai_khoan.Add(newUser);
            await _context.SaveChangesAsync();

            return Ok(new { message = "Đăng ký tài khoản thành công! Vui lòng đăng nhập." });
        }       
    }
}