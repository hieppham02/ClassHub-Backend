using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ClassHub_API.Models
{
    [Table("building")]
    public class ToaNha
    {
        [Key]
        [Column("id")]
        public string ma_toa_nha { get; set; } = null!;

        [Column("name")]
        public string ten_toa_nha { get; set; } = null!;

        [Column("floor_count")]
        public int? so_tang { get; set; } = 5;

        [Column("description")]
        public string? mo_ta { get; set; }

        public virtual ICollection<PhongHoc> PhongHocs { get; set; } = new List<PhongHoc>();
    }
}