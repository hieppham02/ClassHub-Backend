using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ClassHub_API.Models
{
    [Table("phieu_muon")]
    public class PhieuMuon
    {
        [Key]
        [Column("id")]
        public int id { get; set; }

        [Column("ma_sv")]
        public string ma_sv { get; set; } = null!;

        [Column("ma_phong")]
        public string ma_phong { get; set; } = null!;

        [Column("ngay_muon")]
        public DateTime ngay_muon { get; set; }

        [Column("ca_muon")]
        public int ca_muon { get; set; }

        [Column("trang_thai")]
        public string trang_thai { get; set; } = "PENDING";

        [Column("thoi_gian_tao")]
        public DateTime thoi_gian_tao { get; set; } = DateTime.Now;

        [Column("otp")]
        public string? otp { get; set; }

        [Column("otp_expires_at")]
        public DateTime? otp_expires_at { get; set; }

        [Column("thoi_gian_nhan")]
        public DateTime? thoi_gian_nhan { get; set; }

        [Column("thoi_gian_tra")]
        public DateTime? thoi_gian_tra { get; set; }

        [Column("ma_sv_tra")]
        public string? ma_sv_tra { get; set; }

        [Column("ma_sv_uy_quyen")]
        public string? ma_sv_uy_quyen { get; set; }

        [Column("is_cabinet_open")]
        public bool? is_cabinet_open { get; set; } = false;

        [Column("tinh_trang_tra")]
        public string? tinh_trang_tra { get; set; } = "BINH_THUONG";

        [Column("ghi_chu_tra")]
        public string? ghi_chu_tra { get; set; }

        [Column("ly_do_huy")]
        public string? ly_do_huy { get; set; }

        [ForeignKey("ma_phong")]
        public virtual PhongHoc? MaPhongNavigation { get; set; }

        [ForeignKey("ma_sv")]
        public virtual TaiKhoan? MaSvNavigation { get; set; }

        [ForeignKey("ma_sv_tra")]
        public virtual TaiKhoan? MaSvTraNavigation { get; set; }

        [ForeignKey("ma_sv_uy_quyen")]
        public virtual TaiKhoan? MaSvUyQuyenNavigation { get; set; }
    }
}