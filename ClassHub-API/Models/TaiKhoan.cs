using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ClassHub_API.Models
{
    [Table("account")]
    public class TaiKhoan
    {
        [Key]
        [Column("id")]
        public string ma_sv { get; set; } = null!;

        [Column("email")]
        public string email { get; set; } = null!;

        [Column("full_name")]
        public string ho_ten { get; set; } = null!;

        [Column("password_hash")]
        public string mat_khau { get; set; } = null!;

        [Column("phone")]
        public string? sdt { get; set; }

        [Column("role")]
        public string vai_tro { get; set; } = "SINHVIEN";

        [Column("class_or_department")]
        public string? ten_lop { get; set; }

        [Column("is_active")]
        public bool trang_thai { get; set; } = true;

        [Column("created_at")]
        public DateTime ngay_tao { get; set; } = DateTime.Now;

        public virtual ICollection<PhieuMuon> PhieuMuons { get; set; } = new List<PhieuMuon>();
    }
}