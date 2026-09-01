using ClassHub_API.Models;
using Microsoft.EntityFrameworkCore;

namespace ClassHub_API.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
        {
        }

        public DbSet<ToaNha> toa_nha { get; set; } = null!;
        public DbSet<PhongHoc> phong_hoc { get; set; } = null!;
        public DbSet<TaiKhoan> tai_khoan { get; set; } = null!;
        public DbSet<ThietBi> thiet_bi { get; set; } = null!;
        public DbSet<ThietBiIot> thiet_bi_iot { get; set; } = null!;
        public DbSet<CauHinhCaHoc> cau_hinh_ca_hoc { get; set; } = null!;
        public DbSet<PhieuMuon> phieu_muon { get; set; } = null!;
        public DbSet<NhatKyHeThong> nhat_ky_he_thong { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // ToaNha
            modelBuilder.Entity<ToaNha>(entity =>
            {
                entity.HasKey(e => e.ma_toa_nha);
                entity.ToTable("toa_nha");
            });

            // PhongHoc
            modelBuilder.Entity<PhongHoc>(entity =>
            {
                entity.HasKey(e => e.ma_phong);
                entity.ToTable("phong_hoc");

                entity.HasOne(d => d.MaToaNhaNavigation)
                      .WithMany(p => p.PhongHocs)
                      .HasForeignKey(d => d.ma_toa_nha)
                      .OnDelete(DeleteBehavior.SetNull);
            });

            // TaiKhoan
            modelBuilder.Entity<TaiKhoan>(entity =>
            {
                entity.HasKey(e => e.ma_sv);
                entity.ToTable("tai_khoan");
                entity.HasIndex(e => e.email).IsUnique();
            });

            // ThietBi
            modelBuilder.Entity<ThietBi>(entity =>
            {
                entity.HasKey(e => e.id);
                entity.ToTable("thiet_bi");

                entity.HasOne(d => d.MaPhongNavigation)
                      .WithMany(p => p.ThietBis)
                      .HasForeignKey(d => d.ma_phong)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            // ThietBiIot
            modelBuilder.Entity<ThietBiIot>(entity =>
            {
                entity.HasKey(e => e.id);
                entity.ToTable("thiet_bi_iot");
                entity.HasIndex(e => e.mac_address).IsUnique();

                entity.HasOne(d => d.MaPhongNavigation)
                      .WithMany(p => p.ThietBiIots)
                      .HasForeignKey(d => d.ma_phong)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            // CauHinhCaHoc
            modelBuilder.Entity<CauHinhCaHoc>(entity =>
            {
                entity.HasKey(e => e.ca_id);
                entity.ToTable("cau_hinh_ca_hoc");
            });

            // PhieuMuon
            modelBuilder.Entity<PhieuMuon>(entity =>
            {
                entity.HasKey(e => e.id);
                entity.ToTable("phieu_muon");

                entity.HasOne(d => d.MaPhongNavigation)
                      .WithMany(p => p.PhieuMuons)
                      .HasForeignKey(d => d.ma_phong)
                      .OnDelete(DeleteBehavior.Restrict);

                entity.HasOne(d => d.MaSvNavigation)
                      .WithMany(p => p.PhieuMuons)
                      .HasForeignKey(d => d.ma_sv)
                      .OnDelete(DeleteBehavior.Restrict);

                entity.HasOne(d => d.MaSvTraNavigation)
                      .WithMany()
                      .HasForeignKey(d => d.ma_sv_tra)
                      .OnDelete(DeleteBehavior.Restrict);

                entity.HasOne(d => d.MaSvUyQuyenNavigation)
                      .WithMany()
                      .HasForeignKey(d => d.ma_sv_uy_quyen)
                      .OnDelete(DeleteBehavior.Restrict);
            });

            // NhatKyHeThong
            modelBuilder.Entity<NhatKyHeThong>(entity =>
            {
                entity.HasKey(e => e.id);
                entity.ToTable("nhat_ky_he_thong");

                entity.Property(e => e.ma_sv).HasMaxLength(50);
                entity.Property(e => e.hanh_dong).HasMaxLength(100).IsRequired();
                entity.Property(e => e.ip_address).HasMaxLength(50);
                entity.Property(e => e.user_agent).HasMaxLength(255);
                entity.Property(e => e.thoi_gian).HasDefaultValueSql("CURRENT_TIMESTAMP()");

                entity.HasOne(d => d.TaiKhoanNavigation)
                      .WithMany()
                      .HasForeignKey(d => d.ma_sv)
                      .OnDelete(DeleteBehavior.SetNull);
            });
        }
    }
}