using ClassHub_API.Interfaces;
using ClassHub_API.Models;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace ClassHub_API.Services
{
    public class TokenService : ITokenService
    {
        private readonly string _issuer;
        private readonly string _audience;
        private readonly int _accessTokenMinutes;
        private readonly SigningCredentials _signingCredentials;

        public TokenService(IConfiguration configuration)
        {
            string secretKey = configuration["JwtSettings:SecretKey"]
                ?? throw new InvalidOperationException("Thiếu cấu hình JwtSettings:SecretKey.");

            _issuer = configuration["JwtSettings:Issuer"]
                ?? throw new InvalidOperationException("Thiếu cấu hình JwtSettings:Issuer.");

            _audience = configuration["JwtSettings:Audience"]
                ?? throw new InvalidOperationException("Thiếu cấu hình JwtSettings:Audience.");

            if (Encoding.UTF8.GetByteCount(secretKey) < 32)
                throw new InvalidOperationException("JWT SecretKey phải có độ dài tối thiểu 32 byte.");

            _accessTokenMinutes = configuration.GetValue<int?>("JwtSettings:AccessTokenMinutes") ?? 480;

            if (_accessTokenMinutes < 5 || _accessTokenMinutes > 1440)
                throw new InvalidOperationException("AccessTokenMinutes phải nằm trong khoảng từ 5 đến 1440 phút.");

            var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey));
            _signingCredentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);
        }

        public string GenerateToken(TaiKhoan user)
        {
            if (user == null) throw new ArgumentNullException(nameof(user));

            if (string.IsNullOrWhiteSpace(user.ma_sv))
                throw new ArgumentException("Tài khoản không có mã định danh.", nameof(user));

            if (string.IsNullOrWhiteSpace(user.vai_tro))
                throw new ArgumentException("Tài khoản không có vai trò.", nameof(user));

            DateTime issuedAt = DateTime.UtcNow;
            DateTime expiresAt = issuedAt.AddMinutes(_accessTokenMinutes);

            var claims = new List<Claim>
            {
                new(JwtRegisteredClaimNames.Sub, user.ma_sv),
                new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
                new(ClaimTypes.NameIdentifier, user.ma_sv),
                new(ClaimTypes.Name, user.ho_ten),
                new(ClaimTypes.Email, user.email),
                new(ClaimTypes.Role, user.vai_tro.ToUpperInvariant())
            };

            var token = new JwtSecurityToken(
                issuer: _issuer,
                audience: _audience,
                claims: claims,
                notBefore: issuedAt,
                expires: expiresAt,
                signingCredentials: _signingCredentials);

            return new JwtSecurityTokenHandler().WriteToken(token);
        }
    }
}