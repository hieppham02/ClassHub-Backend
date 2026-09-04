using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ClassHub_API.Models
{
    [Table("phong_hoc")]
    public class PhongHoc
    {
        [Key]
        [Column("ma_phong")]
        public string ma_phong { get; set; } = null!;

        [Column("ten_phong")]
        public string ten_phong { get; set; } = null!;

        [Column("tang")]
        public int tang { get; set; } = 1;

        [Column("suc_chua")]
        public int? suc_chua { get; set; } = 70;

        [Column("trang_thai")]
        public string trang_thai { get; set; } = "HOAT_DONG";

        [Column("ma_toa_nha")]
        public string? ma_toa_nha { get; set; }

        [ForeignKey("ma_toa_nha")]
        public virtual ToaNha? MaToaNhaNavigation { get; set; }

        public virtual ICollection<PhieuMuon> PhieuMuons { get; set; } = new List<PhieuMuon>();
        public virtual ICollection<ThietBi> ThietBis { get; set; } = new List<ThietBi>();
        public virtual ICollection<ThietBiIot> ThietBiIots { get; set; } = new List<ThietBiIot>();
    }
}