using ClassHub_API.Models;

namespace ClassHub_API.Interfaces
{
    public interface ITokenService
    {
        public string GenerateToken(TaiKhoan user);
    }
}
