using ClassHub_API.Models;
using Microsoft.EntityFrameworkCore;

namespace ClassHub_API.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
        {
        }

        public DbSet<TaiKhoan> tai_khoan { get; set; }
        public DbSet<PhongHoc> phong_hoc { get; set; }
        public DbSet<ToaNha> toa_nha { get; set; }
        public DbSet<ThietBi> thiet_bi { get; set; }
        public DbSet<PhieuMuon> phieu_muon { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
        }
    }
}
