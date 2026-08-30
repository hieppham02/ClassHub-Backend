using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using Pomelo.EntityFrameworkCore.MySql.Scaffolding.Internal;

namespace ClassHub_API.Models;

public partial class HtqlMuontrathietbiContext : DbContext
{
    public HtqlMuontrathietbiContext()
    {
    }

    public HtqlMuontrathietbiContext(DbContextOptions<HtqlMuontrathietbiContext> options)
        : base(options)
    {
    }

    public virtual DbSet<PhieuMuon> PhieuMuons { get; set; }

    public virtual DbSet<PhongHoc> PhongHocs { get; set; }

    public virtual DbSet<TaiKhoan> TaiKhoans { get; set; }

    public virtual DbSet<ThietBi> ThietBis { get; set; }

    public virtual DbSet<ToaNha> ToaNhas { get; set; }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
#warning To protect potentially sensitive information in your connection string, you should move it out of source code. You can avoid scaffolding the connection string by using the Name= syntax to read it from configuration - see https://go.microsoft.com/fwlink/?linkid=2131148. For more guidance on storing connection strings, see https://go.microsoft.com/fwlink/?LinkId=723263.
        => optionsBuilder.UseMySql("server=localhost;port=3306;database=htql_muontrathietbi;uid=root", Microsoft.EntityFrameworkCore.ServerVersion.Parse("10.4.32-mariadb"));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder
            .UseCollation("utf8mb4_general_ci")
            .HasCharSet("utf8mb4");

        modelBuilder.Entity<PhieuMuon>(entity =>
        {
            entity.HasKey(e => e.id).HasName("PRIMARY");

            entity.ToTable("phieu_muon");

            entity.HasIndex(e => e.ma_phong, "fk_phieu_phong");

            entity.HasIndex(e => e.ma_sv, "fk_phieu_taikhoan");

            entity.Property(e => e.id)
                .HasColumnType("int(11)")
                .HasColumnName("id");
            entity.Property(e => e.ca_muon)
                .HasColumnType("int(11)")
                .HasColumnName("ca_muon");
            entity.Property(e => e.ma_phong)
                .HasMaxLength(50)
                .HasColumnName("ma_phong");
            entity.Property(e => e.ma_sv)
                .HasMaxLength(50)
                .HasColumnName("ma_sv");
            entity.Property(e => e.ngay_muon).HasColumnName("ngay_muon");
            entity.Property(e => e.otp)
                .HasMaxLength(6)
                .HasColumnName("otp");
            entity.Property(e => e.thoi_gian_tao)
                .HasDefaultValueSql("current_timestamp()")
                .HasColumnType("timestamp")
                .HasColumnName("thoi_gian_tao");
            entity.Property(e => e.trang_thai)
                .HasMaxLength(20)
                .HasDefaultValueSql("'ACTIVE'")
                .HasColumnName("trang_thai");

            entity.HasOne(d => d.MaPhongNavigation).WithMany(p => p.PhieuMuons)
                .HasForeignKey(d => d.ma_phong)
                .HasConstraintName("fk_phieu_phong");

            entity.HasOne(d => d.MaSvNavigation).WithMany(p => p.PhieuMuons)
                .HasForeignKey(d => d.ma_sv)
                .HasConstraintName("fk_phieu_taikhoan");
        });

        modelBuilder.Entity<PhongHoc>(entity =>
        {
            entity.HasKey(e => e.ma_phong).HasName("PRIMARY");

            entity.ToTable("phong_hoc");

            entity.HasIndex(e => e.ma_toa_nha, "fk_phong_toanha");

            entity.Property(e => e.ma_phong).HasColumnName("ma_phong");
            entity.Property(e => e.ma_toa_nha)
                .HasMaxLength(50)
                .HasColumnName("ma_toa_nha");
            entity.Property(e => e.suc_chua)
                .HasColumnType("int(11)")
                .HasColumnName("suc_chua");
            entity.Property(e => e.ten_phong)
                .HasMaxLength(255)
                .HasColumnName("ten_phong");
            entity.Property(e => e.trang_thai)
                .HasMaxLength(255)
                .HasColumnName("trang_thai");

            entity.HasOne(d => d.MaToaNhaNavigation).WithMany(p => p.PhongHocs)
                .HasForeignKey(d => d.ma_toa_nha)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("fk_phong_toanha");
        });

        modelBuilder.Entity<TaiKhoan>(entity =>
        {
            entity.HasKey(e => e.ma_sv).HasName("PRIMARY");

            entity.ToTable("tai_khoan");

            entity.Property(e => e.ma_sv)
                .HasMaxLength(50)
                .HasColumnName("ma_sv");
            entity.Property(e => e.email)
                .HasMaxLength(100)
                .HasColumnName("email");
            entity.Property(e => e.ho_ten)
                .HasMaxLength(100)
                .HasColumnName("ho_ten");
            entity.Property(e => e.mat_khau)
                .HasMaxLength(100)
                .HasColumnName("mat_khau");
            entity.Property(e => e.sdt)
                .HasMaxLength(15)
                .HasColumnName("sdt");
            entity.Property(e => e.ten_lop)
                .HasMaxLength(50)
                .HasColumnName("ten_lop");
            entity.Property(e => e.vai_tro)
                .HasMaxLength(20)
                .HasColumnName("vai_tro");
        });

        modelBuilder.Entity<ThietBi>(entity =>
        {
            entity.HasKey(e => e.id).HasName("PRIMARY");

            entity.ToTable("thiet_bi");

            entity.HasIndex(e => e.ma_phong, "fk_thietbi_phong");

            entity.Property(e => e.id)
                .HasColumnType("int(11)")
                .HasColumnName("id");
            entity.Property(e => e.ma_phong)
                .HasMaxLength(50)
                .HasColumnName("ma_phong");
            entity.Property(e => e.so_luong)
                .HasColumnType("int(11)")
                .HasColumnName("so_luong");
            entity.Property(e => e.ten_thiet_bi)
                .HasMaxLength(100)
                .HasColumnName("ten_thiet_bi");

            entity.HasOne(d => d.MaPhongNavigation).WithMany(p => p.ThietBis)
                .HasForeignKey(d => d.ma_phong)
                .HasConstraintName("fk_thietbi_phong");
        });

        modelBuilder.Entity<ToaNha>(entity =>
        {
            entity.HasKey(e => e.ma_toa_nha).HasName("PRIMARY");

            entity.ToTable("toa_nha");

            entity.Property(e => e.ma_toa_nha)
                .HasMaxLength(50)
                .HasColumnName("ma_toa_nha");
            entity.Property(e => e.ten_toa_nha)
                .HasMaxLength(255)
                .HasColumnName("ten_toa_nha");
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
