using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ClassHub_API.Models
{
    [Table("tai_khoan")]
    public class TaiKhoan
    {
        [Key]
        [Column("ma_sv")]
        public string ma_sv { get; set; } = null!;

        [Column("email")]
        public string email { get; set; } = null!;

        [Column("ho_ten")]
        public string ho_ten { get; set; } = null!;

        [Column("mat_khau")]
        public string mat_khau { get; set; } = null!;

        [Column("sdt")]
        public string? sdt { get; set; }

        [Column("vai_tro")]
        public string vai_tro { get; set; } = "SINHVIEN";

        [Column("ten_lop")]
        public string? ten_lop { get; set; }

        [Column("trang_thai")]
        public bool trang_thai { get; set; } = true;

        [Column("ngay_tao")]
        public DateTime ngay_tao { get; set; } = DateTime.Now;

        public virtual ICollection<PhieuMuon> PhieuMuons { get; set; } = new List<PhieuMuon>();
    }
}