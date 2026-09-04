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

            ConfigureBuildings(modelBuilder);
            ConfigureRooms(modelBuilder);
            ConfigureAccounts(modelBuilder);
            ConfigureEquipment(modelBuilder);
            ConfigureIotDevices(modelBuilder);
            ConfigureClassSlots(modelBuilder);
            ConfigureBorrowingSessions(modelBuilder);
            ConfigureAuditLogs(modelBuilder);
        }

        private static void ConfigureBuildings(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ToaNha>(entity =>
            {
                entity.ToTable("toa_nha");
                entity.HasKey(building => building.ma_toa_nha);

                entity.Property(building => building.ma_toa_nha).HasMaxLength(50);
                entity.Property(building => building.ten_toa_nha).HasMaxLength(255).IsRequired();
                entity.Property(building => building.so_tang).HasDefaultValue(5);
                entity.Property(building => building.mo_ta).HasMaxLength(255);
            });
        }

        private static void ConfigureRooms(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<PhongHoc>(entity =>
            {
                entity.ToTable("phong_hoc");
                entity.HasKey(room => room.ma_phong);

                entity.Property(room => room.ma_phong).HasMaxLength(50);
                entity.Property(room => room.ten_phong).HasMaxLength(255).IsRequired();
                entity.Property(room => room.tang).HasDefaultValue(1);
                entity.Property(room => room.suc_chua).HasDefaultValue(70);
                entity.Property(room => room.trang_thai).HasMaxLength(50).HasDefaultValue("HOAT_DONG");
                entity.Property(room => room.ma_toa_nha).HasMaxLength(50);

                entity.HasIndex(room => room.ma_toa_nha).HasDatabaseName("fk_phong_toanha");
                entity.HasIndex(room => room.trang_thai).HasDatabaseName("IX_phong_hoc_trang_thai");

                entity.HasOne(room => room.MaToaNhaNavigation)
                    .WithMany(building => building.PhongHocs)
                    .HasForeignKey(room => room.ma_toa_nha)
                    .OnDelete(DeleteBehavior.SetNull);
            });
        }

        private static void ConfigureAccounts(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TaiKhoan>(entity =>
            {
                entity.ToTable("tai_khoan");
                entity.HasKey(account => account.ma_sv);

                entity.Property(account => account.ma_sv).HasMaxLength(50);
                entity.Property(account => account.email).HasMaxLength(100).IsRequired();
                entity.Property(account => account.ho_ten).HasMaxLength(100).IsRequired();
                entity.Property(account => account.mat_khau).HasMaxLength(255).IsRequired();
                entity.Property(account => account.sdt).HasMaxLength(15);
                entity.Property(account => account.vai_tro).HasMaxLength(20).HasDefaultValue("SINHVIEN");
                entity.Property(account => account.ten_lop).HasMaxLength(50);
                entity.Property(account => account.trang_thai).HasDefaultValue(true);
                entity.Property(account => account.ngay_tao).HasColumnType("timestamp").HasDefaultValueSql("CURRENT_TIMESTAMP");

                entity.HasIndex(account => account.email).IsUnique().HasDatabaseName("email");
                entity.HasIndex(account => new { account.vai_tro, account.trang_thai })
                    .HasDatabaseName("IX_tai_khoan_vai_tro_trang_thai");
            });
        }

        private static void ConfigureEquipment(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ThietBi>(entity =>
            {
                entity.ToTable("thiet_bi");
                entity.HasKey(equipment => equipment.id);

                entity.Property(equipment => equipment.ten_thiet_bi).HasMaxLength(100).IsRequired();
                entity.Property(equipment => equipment.so_luong).HasDefaultValue(1);
                entity.Property(equipment => equipment.tinh_trang).HasMaxLength(50).HasDefaultValue("TOT");
                entity.Property(equipment => equipment.ghi_chu).HasMaxLength(255);
                entity.Property(equipment => equipment.ma_phong).HasMaxLength(50);

                entity.HasIndex(equipment => equipment.ma_phong).HasDatabaseName("fk_thietbi_phong");

                entity.HasOne(equipment => equipment.MaPhongNavigation)
                    .WithMany(room => room.ThietBis)
                    .HasForeignKey(equipment => equipment.ma_phong)
                    .OnDelete(DeleteBehavior.Cascade);
            });
        }

        private static void ConfigureIotDevices(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ThietBiIot>(entity =>
            {
                entity.ToTable("thiet_bi_iot");
                entity.HasKey(device => device.id);

                entity.Property(device => device.ma_phong).HasMaxLength(50);
                entity.Property(device => device.ten_tu).HasMaxLength(100).IsRequired();
                entity.Property(device => device.mac_address).HasMaxLength(50).IsRequired();
                entity.Property(device => device.mqtt_topic).HasMaxLength(255).IsRequired();
                entity.Property(device => device.trang_thai_mang).HasDefaultValue(false);
                entity.Property(device => device.trang_thai_khoa).HasMaxLength(30).HasDefaultValue("LOCKED");
                entity.Property(device => device.firmware_version).HasMaxLength(20).HasDefaultValue("v1.0.0");
                entity.Property(device => device.lan_cuoi_online).HasColumnType("timestamp");

                entity.HasIndex(device => device.mac_address).IsUnique().HasDatabaseName("mac_address");
                entity.HasIndex(device => device.mqtt_topic).IsUnique().HasDatabaseName("UX_thiet_bi_iot_mqtt_topic");
                entity.HasIndex(device => device.ma_phong).IsUnique().HasDatabaseName("UX_thiet_bi_iot_ma_phong");
                entity.HasIndex(device => new { device.trang_thai_mang, device.lan_cuoi_online })
                    .HasDatabaseName("IX_thiet_bi_iot_mang_lan_cuoi_online");

                entity.HasOne(device => device.MaPhongNavigation)
                    .WithMany(room => room.ThietBiIots)
                    .HasForeignKey(device => device.ma_phong)
                    .OnDelete(DeleteBehavior.Cascade);
            });
        }

        private static void ConfigureClassSlots(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<CauHinhCaHoc>(entity =>
            {
                entity.ToTable("cau_hinh_ca_hoc");
                entity.HasKey(slot => slot.ca_id);

                entity.Property(slot => slot.ten_ca).HasMaxLength(50).IsRequired();
                entity.Property(slot => slot.gio_bat_dau).HasColumnType("time");
                entity.Property(slot => slot.gio_ket_thuc).HasColumnType("time");
                entity.Property(slot => slot.ghi_chu).HasMaxLength(255);
            });
        }

        private static void ConfigureBorrowingSessions(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<PhieuMuon>(entity =>
            {
                entity.ToTable("phieu_muon");
                entity.HasKey(session => session.id);

                entity.Property(session => session.ma_sv).HasMaxLength(50).IsRequired();
                entity.Property(session => session.ma_phong).HasMaxLength(50).IsRequired();
                entity.Property(session => session.ngay_muon).HasColumnType("date");
                entity.Property(session => session.trang_thai).HasMaxLength(30).HasDefaultValue("PENDING");
                entity.Property(session => session.thoi_gian_tao).HasColumnType("timestamp").HasDefaultValueSql("CURRENT_TIMESTAMP");
                entity.Property(session => session.otp).HasMaxLength(6);
                entity.Property(session => session.otp_expires_at).HasColumnType("datetime");
                entity.Property(session => session.thoi_gian_nhan).HasColumnType("datetime");
                entity.Property(session => session.thoi_gian_tra).HasColumnType("datetime");
                entity.Property(session => session.ma_sv_tra).HasMaxLength(50);
                entity.Property(session => session.ma_sv_uy_quyen).HasMaxLength(50);
                entity.Property(session => session.is_cabinet_open).HasDefaultValue(false);
                entity.Property(session => session.tinh_trang_tra).HasMaxLength(50).HasDefaultValue("BINH_THUONG");
                entity.Property(session => session.ly_do_huy).HasMaxLength(255);

                entity.HasIndex(session => session.ma_sv).HasDatabaseName("fk_phieu_taikhoan");
                entity.HasIndex(session => session.ma_phong).HasDatabaseName("fk_phieu_phong");
                entity.HasIndex(session => session.ma_sv_tra).HasDatabaseName("fk_phieu_sv_tra");
                entity.HasIndex(session => session.ma_sv_uy_quyen).HasDatabaseName("fk_phieu_sv_uyquyen");
                entity.HasIndex(session => session.ca_muon).HasDatabaseName("IX_phieu_muon_ca_muon");
                entity.HasIndex(session => new { session.ma_sv, session.trang_thai })
                    .HasDatabaseName("IX_phieu_muon_ma_sv_trang_thai");
                entity.HasIndex(session => new
                {
                    session.ma_phong,
                    session.ngay_muon,
                    session.ca_muon,
                    session.trang_thai
                })
                    .HasDatabaseName("IX_phieu_muon_phong_ngay_ca_trang_thai");
                entity.HasIndex(session => session.thoi_gian_tao).HasDatabaseName("IX_phieu_muon_thoi_gian_tao");

                entity.HasOne(session => session.MaPhongNavigation)
                    .WithMany(room => room.PhieuMuons)
                    .HasForeignKey(session => session.ma_phong)
                    .OnDelete(DeleteBehavior.Restrict);

                entity.HasOne(session => session.MaSvNavigation)
                    .WithMany(account => account.PhieuMuons)
                    .HasForeignKey(session => session.ma_sv)
                    .OnDelete(DeleteBehavior.Restrict);

                entity.HasOne(session => session.MaSvTraNavigation)
                    .WithMany()
                    .HasForeignKey(session => session.ma_sv_tra)
                    .OnDelete(DeleteBehavior.Restrict);

                entity.HasOne(session => session.MaSvUyQuyenNavigation)
                    .WithMany()
                    .HasForeignKey(session => session.ma_sv_uy_quyen)
                    .OnDelete(DeleteBehavior.Restrict);

                entity.HasOne<CauHinhCaHoc>()
                    .WithMany()
                    .HasForeignKey(session => session.ca_muon)
                    .OnDelete(DeleteBehavior.Restrict);
            });
        }

        private static void ConfigureAuditLogs(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<NhatKyHeThong>(entity =>
            {
                entity.ToTable("nhat_ky_he_thong");
                entity.HasKey(log => log.id);

                entity.Property(log => log.ma_sv).HasMaxLength(50);
                entity.Property(log => log.hanh_dong).HasMaxLength(100).IsRequired();
                entity.Property(log => log.ip_address).HasMaxLength(50);
                entity.Property(log => log.user_agent).HasMaxLength(255);
                entity.Property(log => log.thoi_gian).HasColumnType("timestamp").HasDefaultValueSql("CURRENT_TIMESTAMP");

                entity.HasIndex(log => log.ma_sv).HasDatabaseName("fk_nhatky_taikhoan");
                entity.HasIndex(log => log.thoi_gian).HasDatabaseName("IX_nhat_ky_he_thong_thoi_gian");

                entity.HasOne(log => log.TaiKhoanNavigation)
                    .WithMany()
                    .HasForeignKey(log => log.ma_sv)
                    .OnDelete(DeleteBehavior.SetNull);
            });
        }
    }
}
