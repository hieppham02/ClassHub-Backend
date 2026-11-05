using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ClassHub_API.Models
{
    [Table("borrowing_session")]
    public class PhieuMuon
    {
        [Key]
        [Column("id")]
        public int id { get; set; }

        [Column("borrower_id")]
        public string ma_sv { get; set; } = null!;

        [Column("room_id")]
        public string ma_phong { get; set; } = null!;

        [Column("borrowing_date")]
        public DateTime ngay_muon { get; set; }

        [Column("slot_id")]
        public int ca_muon { get; set; }

        [Column("status")]
        public string trang_thai { get; set; } = "PENDING";

        [Column("created_at")]
        public DateTime thoi_gian_tao { get; set; } = DateTime.Now;

        [Column("qr_enabled")]
        public bool QrEnabled { get; set; } = true;

        [Column("qr_phase"), MaxLength(40)]
        public string? QrPhase { get; set; }

        [Column("received_at")]
        public DateTime? thoi_gian_nhan { get; set; }

        [Column("returned_at")]
        public DateTime? thoi_gian_tra { get; set; }

        [Column("returner_id")]
        public string? ma_sv_tra { get; set; }

        [Column("delegate_id")]
        public string? ma_sv_uy_quyen { get; set; }

        [Column("is_cabinet_open")]
        public bool? is_cabinet_open { get; set; } = false;

        [Column("return_condition")]
        public string? tinh_trang_tra { get; set; } = "BINH_THUONG";

        [Column("return_note")]
        public string? ghi_chu_tra { get; set; }

        [Column("cancellation_reason")]
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