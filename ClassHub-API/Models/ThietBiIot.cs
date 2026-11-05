using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ClassHub_API.Models
{
    [Table("cabinet")]
    public class ThietBiIot
    {
        [Key]
        [Column("id")]
        public int id { get; set; }

        [Column("room_id")]
        public string? ma_phong { get; set; }

        [Column("name")]
        public string ten_tu { get; set; } = null!;

        [Column("mac_address")]
        public string mac_address { get; set; } = null!;

        [Column("mqtt_topic")]
        public string mqtt_topic { get; set; } = null!;

        [Column("is_online")]
        public bool trang_thai_mang { get; set; } = false;

        [Column("lock_state")]
        public string trang_thai_khoa { get; set; } = "LOCKED";

        [Column("firmware_version")]
        public string? firmware_version { get; set; } = "v1.0.0";

        [Column("last_seen_at")]
        public DateTime? lan_cuoi_online { get; set; }

        [ForeignKey("ma_phong")]
        public virtual PhongHoc? MaPhongNavigation { get; set; }
    }
}