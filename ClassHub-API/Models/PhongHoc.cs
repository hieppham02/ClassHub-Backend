using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ClassHub_API.Models
{
    [Table("room")]
    public class PhongHoc
    {
        [Key]
        [Column("id")]
        public string ma_phong { get; set; } = null!;

        [Column("name")]
        public string ten_phong { get; set; } = null!;

        [Column("floor")]
        public int tang { get; set; } = 1;

        [Column("capacity")]
        public int? suc_chua { get; set; } = 70;

        [Column("status")]
        public string trang_thai { get; set; } = "HOAT_DONG";

        [Column("location_type"), MaxLength(20)]
        public string LocationType { get; set; } = "ROOM";

        [Column("building_id")]
        public string? ma_toa_nha { get; set; }

        [ForeignKey("ma_toa_nha")]
        public virtual ToaNha? MaToaNhaNavigation { get; set; }

        public virtual ICollection<PhieuMuon> PhieuMuons { get; set; } = new List<PhieuMuon>();
        public virtual ICollection<ThietBi> ThietBis { get; set; } = new List<ThietBi>();
        public virtual ICollection<ThietBiIot> ThietBiIots { get; set; } = new List<ThietBiIot>();
    }
}