using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ClassHub_API.Models
{
    [Table("audit_log")]
    public class NhatKyHeThong
    {
        [Key]
        [Column("id")]
        public int id { get; set; }

        [Column("account_id")]
        public string? ma_sv { get; set; }

        [Column("action")]
        public string hanh_dong { get; set; } = null!;

        [Column("details")]
        public string? chi_tiet { get; set; }

        [Column("ip_address")]
        public string? ip_address { get; set; }

        [Column("user_agent")]
        public string? user_agent { get; set; }

        [Column("created_at")]
        public DateTime thoi_gian { get; set; } = DateTime.Now;

        [ForeignKey("ma_sv")]
        public virtual TaiKhoan? TaiKhoanNavigation { get; set; }
    }
}