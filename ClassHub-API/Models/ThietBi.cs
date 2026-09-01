using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ClassHub_API.Models
{
    [Table("thiet_bi")]
    public class ThietBi
    {
        [Key]
        [Column("id")]
        public int id { get; set; }

        [Column("ten_thiet_bi")]
        public string ten_thiet_bi { get; set; } = null!;

        [Column("so_luong")]
        public int so_luong { get; set; } = 1;

        [Column("tinh_trang")]
        public string tinh_trang { get; set; } = "TOT";

        [Column("ghi_chu")]
        public string? ghi_chu { get; set; }

        [Column("ma_phong")]
        public string? ma_phong { get; set; }

        [ForeignKey("ma_phong")]
        public virtual PhongHoc? MaPhongNavigation { get; set; }
    }
}