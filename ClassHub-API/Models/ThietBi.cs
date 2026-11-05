using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ClassHub_API.Models
{
    [Table("equipment")]
    public class ThietBi
    {
        [Key]
        [Column("id")]
        public int id { get; set; }

        [Column("name")]
        public string ten_thiet_bi { get; set; } = null!;

        [Column("quantity")]
        public int so_luong { get; set; } = 1;

        [Column("condition")]
        public string tinh_trang { get; set; } = "TOT";

        [Column("note")]
        public string? ghi_chu { get; set; }

        [Column("room_id")]
        public string? ma_phong { get; set; }

        [Column("category"), MaxLength(50)] public string Category { get; set; } = "Khác";
        [Column("usage_scope"), MaxLength(20)] public string UsageScope { get; set; } = "CABINET";
        [Column("tracking_mode"), MaxLength(20)] public string TrackingMode { get; set; } = "BULK";
        [Column("asset_code"), MaxLength(80)] public string? AssetCode { get; set; }
        [Column("catalog_id")] public int CatalogId { get; set; }
        [ForeignKey(nameof(CatalogId))] public EquipmentCatalog Catalog { get; set; } = null!;
        [Column("catalog_code"), MaxLength(36)] public string CatalogCode { get; set; } = Guid.NewGuid().ToString();
        [Column("version")] public int Version { get; set; } = 1;

        [ForeignKey("ma_phong")]
        public virtual PhongHoc? MaPhongNavigation { get; set; }
    }
}