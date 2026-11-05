using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ClassHub_API.Models
{
    // Expected equipment snapshot, not proof of physical handover or return.
    [Table("borrowing_equipment")]
    public class PhieuMuonThietBi
    {
        [Key]
        public int id { get; set; }
        [Column("session_id")]
        public int phieu_muon_id { get; set; }
        [Column("equipment_id")]
        public int? thiet_bi_id { get; set; }
        [Column("name")]
        public string ten_thiet_bi { get; set; } = null!;
        [Column("quantity")]
        public int so_luong { get; set; }
        [Column("expected_condition")]
        public string tinh_trang_du_kien { get; set; } = null!;

        public virtual PhieuMuon PhieuMuonNavigation { get; set; } = null!;
        public virtual ThietBi? ThietBiNavigation { get; set; }
    }
}
