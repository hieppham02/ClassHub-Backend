using ClassHub_API.Data;
using ClassHub_API.DTOs;
using ClassHub_API.Interfaces;
using ClassHub_API.Models;
using Microsoft.AspNetCore.Mvc;

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
    public IActionResult Login([FromBody] LoginDTO model)
    {
        var user = _context.tai_khoan.FirstOrDefault(u => u.ma_sv == model.MaSV);

        if (user == null || model.MaSV != user.ma_sv || model.MatKhau != user.mat_khau)
        {
            return Unauthorized(new { message = "Sai tài khoản hoặc mật khẩu!" });
        }

        var tokenString = _tokenService.GenerateToken(user);

        return Ok(new
        {
            message = "Đăng nhập thành công!",
            token = tokenString,
            user = new
            {
                name = user.ho_ten,
                email = user.email,
                role = user.vai_tro
                //role = "SINHVIEN"
            }
        });
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterDTO model)
    {
        var existingUser = _context.tai_khoan.FirstOrDefault(u => u.ma_sv == model.MaSV || u.email == model.Email);
        if (existingUser != null)
        {
            return BadRequest(new { message = "Mã sinh viên hoặc Email đã được đăng ký!" });
        }

        var newUser = new TaiKhoan
        {
            ma_sv = model.MaSV,
            ho_ten = model.HoTen,
            email = model.Email,
            mat_khau = model.MatKhau, // Note: Ở dự án thực tế, chỗ này bắt buộc phải Hash mật khẩu (BCrypt)
            sdt = model.Sdt,
            ten_lop = model.TenLop,
            vai_tro = "SINHVIEN"
        };

        _context.tai_khoan.Add(newUser);
        await _context.SaveChangesAsync();

        return Ok(new { message = "Đăng ký tài khoản thành công! Vui lòng đăng nhập." });
    }
}