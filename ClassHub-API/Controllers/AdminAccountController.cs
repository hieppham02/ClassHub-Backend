using ClassHub_API.Data;
using ClassHub_API.Models;
using ClassHub_API.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BCryptNet = BCrypt.Net.BCrypt;

namespace ClassHub_API.Controllers
{
    [Route("api/admin/accounts")]
    [ApiController]
    [Authorize(Roles = "ADMIN")]
    public class AdminAccountController : Controller
    {
        private readonly AppDbContext _context;

        public AdminAccountController(AppDbContext context)
        {
            _context = context;
        }

        // 1. Lấy toàn bộ danh sách tài khoản
        [HttpGet]
        public async Task<IActionResult> GetAllAccounts()
        {
            var rawAccounts = await _context.tai_khoan
                .OrderByDescending(a => a.ngay_tao)
                .ToListAsync();

            var accounts = rawAccounts.Select(a => new
            {
                id = a.ma_sv,
                name = a.ho_ten,
                email = a.email,
                phone = a.sdt ?? "---",
                role = a.vai_tro switch
                {
                    "ADMIN" => "Admin",
                    "GIANGVIEN" => "Giảng viên",
                    _ => "Sinh viên"
                },
                rawRole = a.vai_tro,
                className = a.ten_lop ?? "---",
                isActive = a.trang_thai,
                createdAt = a.ngay_tao.ToString("dd/MM/yyyy")
            }).ToList();

            return Ok(accounts);
        }

        // 2. Thêm mới tài khoản người dùng
        [HttpPost]
        public async Task<IActionResult> CreateAccount([FromBody] CreateAccountDTO dto)
        {
            if (string.IsNullOrWhiteSpace(dto.Id) || string.IsNullOrWhiteSpace(dto.Name) || string.IsNullOrWhiteSpace(dto.Email))
            {
                return BadRequest(new { message = "Vui lòng điền đầy đủ Mã, Họ tên và Email!" });
            }

            var trimmedId = dto.Id.Trim();
            var trimmedEmail = dto.Email.Trim();

            // Kiểm tra trùng mã SV hoặc Email
            var isExist = await _context.tai_khoan.AnyAsync(a => a.ma_sv == trimmedId || a.email == trimmedEmail);
            if (isExist)
            {
                return BadRequest(new { message = "Mã tài khoản hoặc Email đã tồn tại trên hệ thống!" });
            }

            // Mật khẩu mặc định là '123456' nếu không nhập
            string rawPassword = string.IsNullOrWhiteSpace(dto.Password) ? "123456" : dto.Password;
            string hashedPassword = BCryptNet.HashPassword(rawPassword, workFactor: 11);

            string roleDb = dto.Role switch
            {
                "Admin" => "ADMIN",
                "Giảng viên" => "GIANGVIEN",
                _ => "SINHVIEN"
            };

            var newAccount = new TaiKhoan
            {
                ma_sv = trimmedId,
                ho_ten = dto.Name.Trim(),
                email = trimmedEmail,
                mat_khau = hashedPassword,
                sdt = dto.Phone?.Trim(),
                vai_tro = roleDb,
                ten_lop = dto.Role == "Admin" ? "Ban Quản Trị" : dto.ClassOrDept?.Trim(),
                trang_thai = true,
                ngay_tao = DateTime.Now
            };

            _context.tai_khoan.Add(newAccount);
            await _context.SaveChangesAsync();

            return Ok(new { message = $"Đã tạo thành công tài khoản cho {newAccount.ho_ten}!" });
        }

        // 3. Cập nhật thông tin tài khoản
        [HttpPut("{id}")]
        public async Task<IActionResult> UpdateAccount(string id, [FromBody] UpdateAccountDTO dto)
        {
            var user = await _context.tai_khoan.FindAsync(id);
            if (user == null) return NotFound(new { message = "Không tìm thấy tài khoản!" });

            user.ho_ten = dto.Name.Trim();
            user.email = dto.Email.Trim();
            user.sdt = dto.Phone?.Trim();
            user.ten_lop = dto.ClassOrDept?.Trim();

            if (!string.IsNullOrWhiteSpace(dto.Role))
            {
                user.vai_tro = dto.Role switch
                {
                    "Admin" => "ADMIN",
                    "Giảng viên" => "GIANGVIEN",
                    _ => "SINHVIEN"
                };
            }

            // Nếu Admin có nhập mật khẩu mới để Reset cho user
            if (!string.IsNullOrWhiteSpace(dto.NewPassword))
            {
                user.mat_khau = BCryptNet.HashPassword(dto.NewPassword, workFactor: 11);
            }

            await _context.SaveChangesAsync();
            return Ok(new { message = "Cập nhật tài khoản thành công!" });
        }

        // 4. Khóa hoặc Mở khóa tài khoản (Toggle Status)
        [HttpPatch("{id}/toggle-status")]
        public async Task<IActionResult> ToggleAccountStatus(string id)
        {
            var user = await _context.tai_khoan.FindAsync(id);
            if (user == null) return NotFound(new { message = "Không tìm thấy tài khoản!" });

            user.trang_thai = !user.trang_thai;
            await _context.SaveChangesAsync();

            string statusText = user.trang_thai ? "Mở khóa thành công!" : "Đã khóa tài khoản!";
            return Ok(new { message = statusText, isActive = user.trang_thai });
        }

        // 5. Xóa tài khoản
        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteAccount(string id)
        {
            var user = await _context.tai_khoan.FindAsync(id);
            if (user == null) return NotFound(new { message = "Không tìm thấy tài khoản!" });

            // Kiểm tra nếu tài khoản đang có phiếu mượn chưa hoàn thành
            var hasActiveBooking = await _context.phieu_muon
                .AnyAsync(p => (p.ma_sv == id || p.ma_sv_tra == id) && (p.trang_thai == "ACTIVE" || p.trang_thai == "PENDING"));

            if (hasActiveBooking)
            {
                return BadRequest(new { message = "Không thể xóa: Tài khoản này đang có đơn mượn phòng chưa hoàn tất!" });
            }

            _context.tai_khoan.Remove(user);
            await _context.SaveChangesAsync();

            return Ok(new { message = "Đã xóa tài khoản thành công!" });
        }
    }
}
